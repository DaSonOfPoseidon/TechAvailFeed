using TechAvail.Core.Mail;
using TechAvail.Core.Parsing;

namespace TechAvail.Data;

// One feed mail into the store. Returns true when the mail
// should be filed as processed, false for the failed label.
public static class FeedIngest
{
    public static bool Ingest(FeedStore store, FeedMail mail, DateTimeOffset? mailboxReceivedAt)
    {
        long Save(string? filename, ParsedFeed? feed, string? error = null) =>
            store.Save(mail.MessageId, "email", mail.Subject, filename, mail.EmailDate, mailboxReceivedAt, feed, error: error);

        if (mail.Attachments.Count != 1)
        {
            Save(null, null, $"expected 1 CSV attachment, found {mail.Attachments.Count}");
            return false;
        }
        var (filename, data) = mail.Attachments[0];
        ParsedFeed feed;
        try
        {
            feed = FeedParser.Parse(data);
        }
        catch (FeedParseException exc)
        {
            Save(filename, null, exc.Message);
            return false;
        }
        // The blocks store keeps only the diff from the newest run, so a late blocks run (one Gmail held
        // in Spam) would replace newer data. It is recorded as failed and left for a backfill. Legacy
        // slots runs are verbatim copies, so a late one changes nothing newer.
        if (feed.Format == "blocks" && feed.GeneratedAt is { } at && store.NewestGeneratedAt() is { } newest && at < newest)
        {
            Save(filename, null, $"generated {at.UtcDateTime:u}, before the newest snapshot ({newest.UtcDateTime:u})");
            return false;
        }
        Save(filename, feed);
        return true;
    }
}
