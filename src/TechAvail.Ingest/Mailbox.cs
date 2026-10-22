using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using TechAvail.Core.Mail;

namespace TechAvail.Ingest;

public sealed record FetchedMail(UniqueId Uid, byte[] Raw, FeedMail Mail, DateTimeOffset? ReceivedAt);

public interface IMailboxSession : IDisposable
{
    IReadOnlyList<FetchedMail> FetchNew();

    // Read-only (EXAMINE, BODY.PEEK): nothing is flagged or moved.
    IReadOnlyList<FetchedMail> Peek(string label);
    void FileAway(UniqueId uid, string label);
    void EnsureLabels();
    int PurgeProcessed(DateOnly before);

    // The poll only reads the inbox, so it never sees these.
    int CountSpam(DateTimeOffset since);
}

public interface IMailbox
{
    IMailboxSession Open();
}

public sealed class ImapMailbox(IngestSettings settings, ILogger<ImapMailbox> log) : IMailbox
{
    // A stalled Gmail connection once held a poll for minutes. Every IMAP call is cancelled once the session is this
    // old, which drops the connection so the poll fails and the next one starts clean.
    static readonly TimeSpan SessionDeadline = TimeSpan.FromMinutes(2);

    public IMailboxSession Open()
    {
        var deadline = new CancellationTokenSource(SessionDeadline);
        var client = new ImapClient { Timeout = (int)TimeSpan.FromSeconds(30).TotalMilliseconds };
        try
        {
            client.Connect(settings.ImapHost, settings.ImapPort, MailKit.Security.SecureSocketOptions.SslOnConnect, deadline.Token);
            client.Authenticate(settings.ImapUser, settings.ImapPassword, deadline.Token);
        }
        catch
        {
            client.Dispose();
            deadline.Dispose();
            throw;
        }
        return new Session(client, deadline, settings, log);
    }

    sealed class Session(ImapClient client, CancellationTokenSource deadline, IngestSettings settings, ILogger log) : IMailboxSession
    {
        CancellationToken Token => deadline.Token;

        IMailFolder Inbox()
        {
            if (!client.Inbox.IsOpen || client.Inbox.Access != FolderAccess.ReadWrite)
                client.Inbox.Open(FolderAccess.ReadWrite, Token);
            return client.Inbox;
        }

        // Read or unread: someone opening the mail in Gmail mustn't hide it. Every mail the poll handles leaves the inbox,
        // and the store skips a Message-ID it has already seen.
        public IReadOnlyList<FetchedMail> FetchNew()
        {
            var inbox = Inbox();
            SearchQuery query = SearchQuery.SubjectContains(settings.MailSubject);
            if (settings.MailFrom.Length > 0)
                query = query.And(SearchQuery.FromContains(settings.MailFrom));
            var mails = new List<FetchedMail>();
            foreach (var uid in inbox.Search(query, Token))
            {
                var (fetched, message) = Fetch(inbox, uid, Token);
                if (SenderCheck.Rejection(message, settings.MailFrom, settings.AuthservId) is { } rejection)
                {
                    log.LogWarning("rejected uid {Uid}: {Rejection}", uid, rejection);
                    FileAway(uid, settings.FailedLabel);
                    continue;
                }
                mails.Add(fetched);
            }
            return mails;
        }

        public IReadOnlyList<FetchedMail> Peek(string label)
        {
            var folder = client.GetFolder(label, Token);
            folder.Open(FolderAccess.ReadOnly, Token);
            return [.. folder.Search(SearchQuery.All, Token).Select(uid => Fetch(folder, uid, Token).Mail)];
        }

        // MailKit fetches with BODY.PEEK, so the message stays unread until it is filed.
        static (FetchedMail Mail, MimeMessage Message) Fetch(IMailFolder folder, UniqueId uid, CancellationToken token)
        {
            byte[] raw;
            using (var stream = new MemoryStream())
            {
                folder.GetStream(uid, string.Empty, token).CopyTo(stream);
                raw = stream.ToArray();
            }
            var summary = folder.Fetch([uid], MessageSummaryItems.InternalDate | MessageSummaryItems.GMailMessageId, token).FirstOrDefault();
            var message = MimeMessage.Load(new MemoryStream(raw));
            // A UID is per folder, so it changes when FileAway moves the mail; Gmail's X-GM-MSGID doesn't.
            var fallbackId = summary?.GMailMessageId is { } gm ? $"gm:{gm}" : $"uid:{uid.Id}";
            return (new FetchedMail(uid, raw, FeedMail.FromMime(message, fallbackId), summary?.InternalDate), message);
        }

        // Gmail: copying to a label and expunging from INBOX archives the message under that label.
        public void FileAway(UniqueId uid, string label)
        {
            var inbox = Inbox();
            inbox.CopyTo(uid, client.GetFolder(label, Token), Token);
            inbox.AddFlags(uid, MessageFlags.Seen | MessageFlags.Deleted, silent: true, Token);
            inbox.Expunge([uid], Token);
        }

        public void EnsureLabels()
        {
            var root = client.GetFolder(client.PersonalNamespaces[0]);
            var existing = root.GetSubfolders(cancellationToken: Token).Select(f => f.FullName).ToHashSet();
            foreach (var label in new[] { settings.ProcessedLabel, settings.FailedLabel }.Where(l => !existing.Contains(l)))
                root.Create(label, isMessageFolder: true, Token);
        }

        // Permanently deletes processed feed mail received before `before`. Expunging from a Gmail
        // label only removes the label, so it goes to Trash first and is expunged there too.
        public int PurgeProcessed(DateOnly before)
        {
            var trash = (client.Capabilities & ImapCapabilities.SpecialUse) != 0 ? client.GetFolder(SpecialFolder.Trash) : null;
            if (trash is null)
                throw new InvalidOperationException("could not find the Gmail Trash folder");
            // Only feed mail: anything else filed under the label, or sitting in Trash, is left alone.
            var query = SearchQuery.DeliveredBefore(before.ToDateTime(TimeOnly.MinValue)).And(SearchQuery.SubjectContains(settings.MailSubject));
            var processed = client.GetFolder(settings.ProcessedLabel, Token);
            processed.Open(FolderAccess.ReadWrite, Token);
            var uids = processed.Search(query, Token);
            if (uids.Count == 0)
                return 0;
            processed.CopyTo(uids, trash, Token);
            processed.AddFlags(uids, MessageFlags.Deleted, silent: true, Token);
            processed.Expunge(uids, Token);
            trash.Open(FolderAccess.ReadWrite, Token);
            var trashed = trash.Search(query, Token);
            if (trashed.Count > 0)
            {
                trash.AddFlags(trashed, MessageFlags.Deleted, silent: true, Token);
                trash.Expunge(trashed, Token);
            }
            return uids.Count;
        }

        public int CountSpam(DateTimeOffset since)
        {
            var spam = (client.Capabilities & ImapCapabilities.SpecialUse) != 0 ? client.GetFolder(SpecialFolder.Junk) : null;
            if (spam is null)
                throw new InvalidOperationException("could not find the Gmail Spam folder");
            spam.Open(FolderAccess.ReadOnly, Token);
            // IMAP's SINCE is a whole day, so the exact cut-off is applied to the delivery times.
            var query = SearchQuery
                .DeliveredAfter(since.UtcDateTime.Date.AddDays(-1))
                .And(SearchQuery.SubjectContains(settings.MailSubject))
                .And(SearchQuery.FromContains(settings.MailFrom));
            var uids = spam.Search(query, Token);
            if (uids.Count == 0)
                return 0;
            // Only mail the inbox would also accept, so a forged or look-alike sender doesn't raise the alert.
            return spam.Fetch(uids, MessageSummaryItems.InternalDate | MessageSummaryItems.Headers, Token)
                .Count(m =>
                    m.InternalDate >= since
                    && m.Headers is { } headers
                    && SenderCheck.Rejection(new MimeMessage(headers.ToArray<object>()), settings.MailFrom, settings.AuthservId) is null
                );
        }

        public void Dispose()
        {
            // A cancelled call has already dropped the connection, so this only quits a live one.
            if (client.IsConnected && !Token.IsCancellationRequested)
                client.Disconnect(quit: true);
            client.Dispose();
            deadline.Dispose();
        }
    }
}
