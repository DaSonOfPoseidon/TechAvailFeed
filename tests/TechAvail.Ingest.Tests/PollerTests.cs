using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using MailKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TechAvail.Core.Mail;
using TechAvail.Data;
using TechAvail.Data.Tests;

namespace TechAvail.Ingest.Tests;

// The poll loop against an in-memory mailbox.
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

        public IReadOnlyList<FetchedMail> Peek(string label) => [];

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

public class PollWorkerTests
{
    // The first poll fails to connect. The second waits for Proceed, so the test can see the recorded
    // error before it is cleared; later polls find an empty inbox.
    sealed class FlakyMailbox : IMailbox, IMailboxSession
    {
        int opens;

        public SemaphoreSlim Proceed { get; } = new(0);

        public int Opens => Volatile.Read(ref opens);

        public IMailboxSession Open()
        {
            var n = Interlocked.Increment(ref opens);
            if (n == 1)
                throw new InvalidOperationException("IMAP login failed");
            if (n == 2)
                Proceed.Wait();
            return this;
        }

        public IReadOnlyList<FetchedMail> FetchNew() => [];

        public IReadOnlyList<FetchedMail> Peek(string label) => [];

        public void FileAway(UniqueId uid, string label) { }

        public void EnsureLabels() { }

        public int PurgeProcessed(DateOnly before) => 0;

        public void Dispose() { }
    }

    static PollWorker Worker(bool mail, FlakyMailbox mailbox, PollState state)
    {
        var settings = IngestSettings.From(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        // An empty inbox never touches the database.
                        ["DATABASE_URL"] = "Host=unused",
                        ["IMAP_USER"] = mail ? "feed@example.com" : "",
                        ["IMAP_PASSWORD"] = "x",
                        ["MAIL_SUBJECT"] = "TechAvailFeed",
                        ["MAIL_FROM"] = "mbs@example.com",
                        ["POLL_SECONDS"] = "0",
                    }
                )
                .Build()
        );
        var poller = new Poller(new FeedStore(settings.ConnectionString), mailbox, settings, TimeProvider.System, NullLogger<Poller>.Instance);
        return new PollWorker(poller, state, settings, TimeProvider.System, NullLogger<PollWorker>.Instance);
    }

    static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task A_failed_poll_is_recorded_and_the_next_one_clears_it()
    {
        var (mailbox, state) = (new FlakyMailbox(), new PollState());
        using var worker = Worker(mail: true, mailbox, state);
        await worker.StartAsync(CancellationToken.None);

        await Until(() => state.Get().Error is not null);
        var (failedAt, error) = state.Get();
        Assert.Equal("IMAP login failed", error);

        mailbox.Proceed.Release();
        await Until(() => state.Get().Error is null);
        Assert.True(state.Get().At >= failedAt);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Without_mail_settings_the_worker_never_polls()
    {
        var (mailbox, state) = (new FlakyMailbox(), new PollState());
        using var worker = Worker(mail: false, mailbox, state);
        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, mailbox.Opens);
        Assert.Null(state.Get().At);
    }
}

public class MailArchiveTests
{
    [Fact]
    public void Archive_keeps_the_raw_mail_once_with_its_metadata()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        var received = new DateTimeOffset(2026, 10, 5, 14, 44, 45, TimeSpan.Zero);
        Assert.True(MailArchive.Save(directory, "<abc@example.com>", received, "raw"u8.ToArray()));
        Assert.False(MailArchive.Save(directory, "<abc@example.com>", received, "raw"u8.ToArray()));
        // The first 32 hex digits of the Message-ID's SHA-256.
        const string name = "a1c01306268e0d2c69f4426068f5e4d0";
        Assert.Equal("raw", File.ReadAllText(Path.Combine(directory, $"{name}.eml")));
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, $"{name}.json")))!;
        Assert.Equal(("<abc@example.com>", "2026-10-05T14:44:45+00:00"), ((string)meta["message_id"]!, (string)meta["mailbox_received_at"]!));
    }
}
