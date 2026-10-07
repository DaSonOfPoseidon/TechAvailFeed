using TechAvail.Data;

namespace TechAvail.Ingest;

// The ingest's settings, from the environment (see .env.example).
public sealed record IngestSettings(
    string ConnectionString,
    string ImapHost,
    int ImapPort,
    string ImapUser,
    string ImapPassword,
    string MailSubject,
    string MailFrom,
    string AuthservId,
    string ProcessedLabel,
    string FailedLabel,
    TimeZoneInfo Tz,
    int PollSeconds,
    int HttpPort,
    string ArchiveDir,
    string ApiKey
)
{
    // The sender is required: subject alone would let anyone inject a feed.
    public bool MailConfigured => ImapUser.Length > 0 && ImapPassword.Length > 0 && MailSubject.Length > 0 && MailFrom.Length > 0;

    public static IngestSettings From(IConfiguration config)
    {
        string Get(string name, string fallback = "") => config[name] is { Length: > 0 } value ? value : fallback;
        return new(
            ConnectionStrings.FromUrl(Get("DATABASE_URL") is { Length: > 0 } url ? url : throw new InvalidOperationException("DATABASE_URL is not set")),
            Get("IMAP_HOST", "imap.gmail.com"),
            int.Parse(Get("IMAP_PORT", "993")),
            Get("IMAP_USER"),
            Get("IMAP_PASSWORD"),
            Get("MAIL_SUBJECT", "TechAvailFeed"),
            Get("MAIL_FROM"),
            // The receiving server whose Authentication-Results header is trusted (Gmail's).
            Get("AUTHSERV_ID", "mx.google.com"),
            Get("PROCESSED_LABEL", "techavail-processed"),
            Get("FAILED_LABEL", "techavail-failed"),
            // The source's local time: block timestamps use it, as does the mail purge's "today".
            TimeZoneInfo.FindSystemTimeZoneById(Get("MAIL_TZ", "America/Chicago")),
            int.Parse(Get("POLL_SECONDS", "60")),
            int.Parse(Get("HTTP_PORT", "8000")),
            // Optional: keep a raw copy of every processed mail (the local corpus).
            Get("ARCHIVE_DIR"),
            Get("API_KEY")
        );
    }
}
