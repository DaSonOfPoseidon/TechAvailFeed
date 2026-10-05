using System.Text;
using Dapper;
using MailKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TechAvail.Core.Mail;
using TechAvail.Data;
using TechAvail.Data.Tests;

namespace TechAvail.Ingest.Tests;

// The poll loop of feed/__main__.py against an in-memory mailbox.
public class PollerTests
{
    const string Csv =
        "GENERATED_AT,KIND,WORK_DATE,TECH_ID,TECH_NAME,STARTS_AT,ENDS_AT,REF_ID,STATUS,REGION,SKILLS\n"
        + "2026-10-06T06:15:02-05,shift,2026-10-06,jdoe0170,Jane Doe,2026-10-06 08:00,2026-10-06 17:00,,,North,INS\n";

    sealed class FakeMailbox(List<FetchedMail> mails) : IMailbox, IMailboxSession
    {
        public List<(uint Uid, string Label)> Filed { get; } = [];
        public List<DateOnly> Purged { get; } = [];

        public IMailboxSession Open() => this;

        public IReadOnlyList<FetchedMail> FetchNew() => mails;

        public void FileAway(UniqueId uid, string label) => Filed.Add((uid.Id, label));

        public void EnsureLabels() { }

        public int PurgeProcessed(DateOnly before)
        {
            Purged.Add(before);
            return 0;
        }

        public void Dispose() { }
    }

    static FetchedMail Mail(uint uid, string messageId, params (string, string)[] attachments) =>
        new(
            new UniqueId(uid),
            Encoding.UTF8.GetBytes($"raw {messageId}"),
            new FeedMail(messageId, "TechAvailFeed", null, [.. attachments.Select(a => (a.Item1, Encoding.UTF8.GetBytes(a.Item2)))]),
            new DateTimeOffset(2026, 10, 6, 11, 16, 0, TimeSpan.Zero)
        );

    static IngestSettings Settings(TestDatabase db, string archive = "") =>
        IngestSettings.From(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["DATABASE_URL"] = db.ConnectionString, ["ARCHIVE_DIR"] = archive })
                .Build()
        );

    [DbFact]
    public void Feed_mail_is_ingested_once_and_filed_by_outcome()
    {
        using var db = new TestDatabase();
        var archive = Directory.CreateTempSubdirectory().FullName;
        var store = new FeedStore(db.ConnectionString);
        var mailbox = new FakeMailbox(
            [
                Mail(1, "<good>", ("f.csv", Csv)),
                Mail(2, "<bad>", ("f.csv", "not,a,feed\n")),
                Mail(3, "<none>"),
                Mail(4, "<good>", ("f.csv", Csv)), // a duplicate delivery
            ]
        );
        // 03:30 UTC is still the 5th in Chicago: the purge's "today" is local.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 6, 3, 30, 0, TimeSpan.Zero));
        new Poller(store, mailbox, Settings(db, archive), clock, NullLogger<Poller>.Instance).PollOnce();

        Assert.Equal(
            [(1u, "techavail-processed"), (2u, "techavail-failed"), (3u, "techavail-failed"), (4u, "techavail-processed")],
            mailbox.Filed
        );
        Assert.Equal([new DateOnly(2026, 10, 5)], mailbox.Purged);
        using var connection = db.Open();
        Assert.Equal(
            [("<good>", "ok", (string?)null), ("<bad>", "error", "missing columns: generated_at, work_date, tech_id, tech_name, open_from, open_until, open_minutes, region, skills"), ("<none>", "error", "expected 1 CSV attachment, found 0")],
            connection.Query<(string, string, string?)>("SELECT message_id, status, error FROM snapshots ORDER BY id")
        );
        // Only processed mail is archived, once.
        Assert.Single(Directory.GetFiles(archive, "*.eml"));
    }
}

public class MailArchiveTests
{
    [Fact]
    public void Archive_layout_matches_the_python_ingest()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        var received = new DateTimeOffset(2026, 10, 5, 14, 44, 45, TimeSpan.Zero);
        Assert.True(MailArchive.Save(directory, "<abc@example.com>", received, "raw"u8.ToArray()));
        Assert.False(MailArchive.Save(directory, "<abc@example.com>", received, "raw"u8.ToArray()));
        // hashlib.sha256(b"<abc@example.com>").hexdigest()[:32]
        const string name = "a1c01306268e0d2c69f4426068f5e4d0";
        Assert.Equal("raw", File.ReadAllText(Path.Combine(directory, $"{name}.eml")));
        Assert.Equal(
            "{\n \"message_id\": \"<abc@example.com>\",\n \"mailbox_received_at\": \"2026-10-05T14:44:45+00:00\"\n}",
            File.ReadAllText(Path.Combine(directory, $"{name}.json")).ReplaceLineEndings("\n")
        );
    }
}
