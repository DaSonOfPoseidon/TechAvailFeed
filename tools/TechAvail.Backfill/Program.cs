using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Npgsql;
using TechAvail.Core.Mail;
using TechAvail.Core.Parsing;
using TechAvail.Data;
using TechAvail.Ingest;

// Backfills feed runs that reached the mailbox late (Gmail held them in Spam), which ingest recorded as
// failed rather than load over newer runs. The store is rewound to the first late run, then every run
// from there is replayed in generation order: the late mail from the failed label, the rest from the
// raw copies in corpus/mail. Each replayed run keeps its ingest time, and every run that was already
// loaded must rebuild exactly as before. A dry run unless --apply; stop ingest before applying.
//
//   scripts/backfill.sh [--apply]
var apply = args.Contains("--apply");
var archive = Path.Combine("corpus", "mail");
var settings = IngestSettings.From(new ConfigurationBuilder().AddEnvironmentVariables().Build());
var store = new FeedStore(settings.ConnectionString);

List<FetchedMail> late;
using (var session = new ImapMailbox(settings, NullLogger<ImapMailbox>.Instance).Open())
    late = [.. session.Peek(settings.FailedLabel)];

// The late runs: failed mail that ingest refused only for arriving after a newer run.
var lateErrors = Query("SELECT message_id, id FROM snapshots WHERE status = 'error' AND error LIKE 'generated %, before the newest snapshot%'")
    .ToDictionary(r => (string)r[0], r => (long)r[1]);
late = [.. late.Where(m => lateErrors.ContainsKey(m.Mail.MessageId)).DistinctBy(m => m.Mail.MessageId)];
if (late.Count == 0)
{
    Console.WriteLine("no late runs under the failed label");
    return 0;
}
var runs = late.Select(m => (m.Mail, m.ReceivedAt, Feed: Parse(m.Mail), Late: true)).ToList();
var from = runs.Min(r => r.Feed.GeneratedAt!.Value);
var fromId = (long)Query("SELECT min(id) FROM snapshots WHERE status = 'ok' AND generated_at > @from", ("from", from.ToUniversalTime()))[0][0];

var loaded = Query("SELECT message_id, status, id FROM snapshots WHERE id >= @id ORDER BY id", ("id", fromId));
foreach (var row in loaded.Where(r => !lateErrors.ContainsKey((string)r[0])))
{
    if ((string)row[1] != "ok")
        throw new InvalidOperationException($"snapshot {row[2]} is {row[1]}, and only ok runs are archived to replay");
    var (mail, received) = Archived((string)row[0]);
    runs.Add((mail, received, Parse(mail), false));
}
runs = [.. runs.OrderBy(r => r.Feed.GeneratedAt)];
Console.WriteLine($"rewind from snapshot {fromId} ({from.UtcDateTime:u}); replay {runs.Count} runs: {late.Count} late, {runs.Count - late.Count} already loaded");
Console.WriteLine($"through {runs[^1].Feed.GeneratedAt!.Value.UtcDateTime:u}");
if (!apply)
{
    Console.WriteLine("dry run: pass --apply to rewind and replay");
    return 0;
}

var before = loaded.Where(r => (string)r[1] == "ok").ToDictionary(r => (string)r[0], r => Fingerprint((long)r[2]));
var times = store.Rewind(fromId);
foreach (var (mail, received, _, isLate) in runs)
{
    if (!FeedIngest.Ingest(store, mail, received))
        throw new InvalidOperationException($"{mail.MessageId} failed on replay; restore the backup taken before the backfill");
    if (isLate)
        MailArchive.Save(archive, mail.MessageId, received, late.Single(m => m.Mail.MessageId == mail.MessageId).Raw);
}
store.SetIngestedAt(times);

var after = Query("SELECT message_id, id FROM snapshots WHERE id > @id AND status = 'ok'", ("id", fromId - 1))
    .ToDictionary(r => (string)r[0], r => Fingerprint((long)r[1]));
var changed = before.Where(b => after.GetValueOrDefault(b.Key) != b.Value).Select(b => b.Key).ToList();
Console.WriteLine($"replayed {after.Count} runs; {before.Count - changed.Count} of {before.Count} already-loaded runs rebuild unchanged");
foreach (var id in changed)
    Console.WriteLine($"CHANGED: {id}");
return changed.Count == 0 ? 0 : 1;

ParsedFeed Parse(FeedMail mail) =>
    mail.Attachments is [var (_, data)] ? FeedParser.Parse(data) : throw new InvalidOperationException($"{mail.MessageId} has no single CSV");

(FeedMail, DateTimeOffset?) Archived(string messageId)
{
    var name = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(messageId)))[..32];
    var eml = Path.Combine(archive, $"{name}.eml");
    if (!File.Exists(eml))
        throw new InvalidOperationException($"{messageId} is not archived in {archive}");
    var meta = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.ChangeExtension(eml, ".json")))!;
    var received = meta["mailbox_received_at"]?.GetValue<string>() is { } at ? DateTimeOffset.Parse(at) : (DateTimeOffset?)null;
    return (FeedMail.FromMime(MimeMessage.Load(eml), "archive"), received);
}

// A hash of the rows a snapshot reconstructs to, independent of row ids.
string Fingerprint(long snapshot) =>
    (string)
        Query(
            $"SELECT md5(coalesce(string_agg(ROW({FeedStore.BlockColumns})::text, E'\\n' ORDER BY ROW({FeedStore.BlockColumns})::text), '')) "
                + $"FROM blocks WHERE {FeedStore.AtSnapshot}",
            ("snapshot", snapshot)
        )[0][0];

List<object[]> Query(string sql, params (string Name, object Value)[] parameters)
{
    using var connection = store.Open();
    using var command = new NpgsqlCommand(sql, connection);
    foreach (var (name, value) in parameters)
        command.Parameters.AddWithValue(name, value);
    using var reader = command.ExecuteReader();
    var rows = new List<object[]>();
    while (reader.Read())
    {
        var row = new object[reader.FieldCount];
        reader.GetValues(row);
        rows.Add(row);
    }
    return rows;
}
