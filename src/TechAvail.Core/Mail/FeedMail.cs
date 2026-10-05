using System.Text;
using MimeKit;
using MimeKit.Utils;

namespace TechAvail.Core.Mail;

// Port of feed/mail.py's to_feed_mail: the parts of a feed email the ingest stores.
public sealed record FeedMail(
    string MessageId,
    string Subject,
    DateTimeOffset? EmailDate,
    IReadOnlyList<(string FileName, byte[] Data)> Attachments
)
{
    public static FeedMail FromMime(MimeMessage message, string uid) =>
        new(
            // Some senders omit Message-ID; the IMAP UID is stable enough within one mailbox.
            PyStrip(RawHeader(message, "Message-ID") ?? $"uid:{uid}"),
            RawHeader(message, "Subject") ?? "",
            MailDate(RawHeader(message, "Date")),
            CsvAttachments(message)
        );

    // An unparsable Date header gives no send time rather than failing the poll.
    static DateTimeOffset? MailDate(string? header) =>
        header is not null && DateUtils.TryParse(header, out var date) ? date : null;

    static List<(string, byte[])> CsvAttachments(MimeMessage message)
    {
        var found = new List<(string, byte[])>();
        using var iterator = new MimeIterator(message);
        while (iterator.MoveNext())
        {
            if (iterator.Current is not MimePart part || part.Content is null)
                continue;
            var name = part.FileName;
            if (name is null || !name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                continue;
            using var data = new MemoryStream();
            part.Content.DecodeTo(data);
            found.Add((name, data.ToArray()));
        }
        return found;
    }

    // A header as Python's compat32 parser returns it: the text after the colon, leading blanks
    // and trailing line breaks removed, folding kept. First occurrence; null when absent.
    internal static string? RawHeader(MimeMessage message, string field) =>
        RawHeaders(message, field).Cast<string?>().FirstOrDefault();

    internal static IEnumerable<string> RawHeaders(MimeMessage message, string field) =>
        message
            .Headers.Where(h => h.Field.Equals(field, StringComparison.OrdinalIgnoreCase))
            .Select(h => Encoding.Latin1.GetString(h.RawValue).TrimStart(' ', '\t').TrimEnd('\r', '\n'));

    static string PyStrip(string value) => Parsing.PyText.Strip(value);
}
