using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using TechAvail.Core.Mail;

namespace TechAvail.Ingest;

// A fetched feed mail: its IMAP uid, raw bytes, parsed form and when the mailbox received it.
public sealed record FetchedMail(UniqueId Uid, byte[] Raw, FeedMail Mail, DateTimeOffset? ReceivedAt);

// One poll's connection to the mailbox. Port of feed/mail.py's Mailbox; an interface so the poll
// loop can be tested without Gmail.
public interface IMailboxSession : IDisposable
{
    IReadOnlyList<FetchedMail> FetchNew();
    void FileAway(UniqueId uid, string label);
    void EnsureLabels();
    int PurgeProcessed(DateOnly before);
}

public interface IMailbox
{
    IMailboxSession Open();
}

public sealed class ImapMailbox(IngestSettings settings, ILogger<ImapMailbox> log) : IMailbox
{
    public IMailboxSession Open()
    {
        var client = new ImapClient();
        client.Connect(settings.ImapHost, settings.ImapPort, MailKit.Security.SecureSocketOptions.SslOnConnect);
        client.Authenticate(settings.ImapUser, settings.ImapPassword);
        return new Session(client, settings, log);
    }

    sealed class Session(ImapClient client, IngestSettings settings, ILogger log) : IMailboxSession
    {
        IMailFolder Inbox()
        {
            if (!client.Inbox.IsOpen || client.Inbox.Access != FolderAccess.ReadWrite)
                client.Inbox.Open(FolderAccess.ReadWrite);
            return client.Inbox;
        }

        public IReadOnlyList<FetchedMail> FetchNew()
        {
            var inbox = Inbox();
            var query = SearchQuery.NotSeen.And(SearchQuery.SubjectContains(settings.MailSubject));
            if (settings.MailFrom.Length > 0)
                query = query.And(SearchQuery.FromContains(settings.MailFrom));
            var mails = new List<FetchedMail>();
            foreach (var uid in inbox.Search(query))
            {
                // MailKit fetches with BODY.PEEK, so the message stays unread until it is filed.
                byte[] raw;
                using (var stream = new MemoryStream())
                {
                    inbox.GetStream(uid, string.Empty).CopyTo(stream);
                    raw = stream.ToArray();
                }
                var received = inbox.Fetch([uid], MessageSummaryItems.InternalDate).FirstOrDefault()?.InternalDate;
                var message = MimeMessage.Load(new MemoryStream(raw));
                if (SenderCheck.Rejection(message, settings.MailFrom, settings.AuthservId) is { } rejection)
                {
                    // Filed away unread, so it isn't fetched again and stays there for inspection.
                    log.LogWarning("rejected uid {Uid}: {Rejection}", uid, rejection);
                    FileAway(uid, settings.FailedLabel);
                    continue;
                }
                mails.Add(new FetchedMail(uid, raw, FeedMail.FromMime(message, uid.Id.ToString()), received));
            }
            return mails;
        }

        // Gmail: copying to a label and expunging from INBOX archives the message under that label.
        public void FileAway(UniqueId uid, string label)
        {
            var inbox = Inbox();
            inbox.CopyTo(uid, client.GetFolder(label));
            inbox.AddFlags(uid, MessageFlags.Seen | MessageFlags.Deleted, silent: true);
            inbox.Expunge([uid]);
        }

        public void EnsureLabels()
        {
            var root = client.GetFolder(client.PersonalNamespaces[0]);
            var existing = root.GetSubfolders().Select(f => f.FullName).ToHashSet();
            foreach (var label in new[] { settings.ProcessedLabel, settings.FailedLabel }.Where(l => !existing.Contains(l)))
                root.Create(label, isMessageFolder: true);
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
            var processed = client.GetFolder(settings.ProcessedLabel);
            processed.Open(FolderAccess.ReadWrite);
            var uids = processed.Search(query);
            if (uids.Count == 0)
                return 0;
            processed.CopyTo(uids, trash);
            processed.AddFlags(uids, MessageFlags.Deleted, silent: true);
            processed.Expunge(uids);
            trash.Open(FolderAccess.ReadWrite);
            var trashed = trash.Search(query);
            if (trashed.Count > 0)
            {
                trash.AddFlags(trashed, MessageFlags.Deleted, silent: true);
                trash.Expunge(trashed);
            }
            return uids.Count;
        }

        public void Dispose()
        {
            if (client.IsConnected)
                client.Disconnect(quit: true);
            client.Dispose();
        }
    }
}
