using System.Text;
using MimeKit;
using MimeKit.Utils;

namespace TechAvail.Core.Mail;

public sealed record FeedMail(
    string MessageId,
    string Subject,
    DateTimeOffset? EmailDate,
    IReadOnlyList<(string FileName, byte[] Data)> Attachments
)
{
    // Some senders omit Message-ID; the mailbox's own id for the message stands in (fallbackId). It must
    // stay the same when the mail moves between labels, so the backfill can find it again.
    public static FeedMail FromMime(MimeMessage message, string fallbackId) =>
        new(
            (RawHeader(message, "Message-ID") ?? fallbackId).Trim(),
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

    // A header's raw text: after the colon, leading blanks and trailing line breaks removed, folding
    // kept (the Message-ID dedupe key depends on it). First occurrence; null when absent.
    internal static string? RawHeader(MimeMessage message, string field) =>
        RawHeaders(message, field).Cast<string?>().FirstOrDefault();

    internal static IEnumerable<string> RawHeaders(MimeMessage message, string field) =>
        message
            .Headers.Where(h => h.Field.Equals(field, StringComparison.OrdinalIgnoreCase))
            .Select(h => Encoding.Latin1.GetString(h.RawValue).TrimStart(' ', '\t').TrimEnd('\r', '\n'));

}
