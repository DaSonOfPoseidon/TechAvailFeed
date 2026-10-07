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
        Save(filename, feed);
        return true;
    }
}
