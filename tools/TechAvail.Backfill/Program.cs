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
// loaded must rebuild exactly as before. A dry run unless --apply; stop ingest and the API before
// applying, as each replayed run commits on its own and the API would serve the partial store.
//
//   scripts/backfill.sh [--apply]
var apply = args.Contains("--apply");
var archive = Path.Combine("corpus", "mail");
var settings = IngestSettings.From(new ConfigurationBuilder().AddEnvironmentVariables().Build());
var store = new FeedStore(settings.ConnectionString);

List<FetchedMail> late;
using (var session = new ImapMailbox(settings, NullLogger<ImapMailbox>.Instance).Open())
    late = [.. session.Peek(settings.FailedLabel)];

// The late runs: failed mail that ingest refused only for arriving after a newer run. The failed label
// also holds mail SenderCheck rejected, so each candidate must pass it as the inbox poll does.
var lateErrors = Query("SELECT message_id, id FROM snapshots WHERE status = 'error' AND error LIKE 'generated %, before the newest snapshot%'")
    .ToDictionary(r => (string)r[0], r => (long)r[1]);
late =
[
    .. late.Where(m =>
            lateErrors.ContainsKey(m.Mail.MessageId)
            && SenderCheck.Rejection(MimeMessage.Load(new MemoryStream(m.Raw)), settings.MailFrom, settings.AuthservId) is null
        )
        .DistinctBy(m => m.Mail.MessageId),
];
if (late.Count == 0)
{
    Console.WriteLine("no late runs under the failed label");
    return 0;
}
// Each run is replayed at its generation time. A header-only run has none, so it keeps its place after
// the run loaded before it (an empty blocks snapshot rebuilds to the blocks open at its position).
var runs = late.Select(m => (m.Mail, m.ReceivedAt, At: Parse(m.Mail).GeneratedAt!.Value, Late: true)).ToList();
var from = runs.Min(r => r.At);
var fromId = (long)Query("SELECT min(id) FROM snapshots WHERE status = 'ok' AND format = 'blocks' AND generated_at > @from", ("from", from.ToUniversalTime()))[0][0];

// The rewind deletes every snapshot from fromId on, so each must be replayed: a late run from its mail,
// any other loaded run from the archive (ingest archives ok and empty runs).
var lateIds = late.Select(m => m.Mail.MessageId).ToHashSet();
var previous = from;
var loaded = Query("SELECT message_id, status, id, format FROM snapshots WHERE id >= @id ORDER BY id", ("id", fromId));
foreach (var row in loaded.Where(r => !lateIds.Contains((string)r[0])))
{
    if ((string)row[1] is not ("ok" or "empty"))
        throw new InvalidOperationException(
            lateErrors.ContainsKey((string)row[0])
                ? $"late run {row[0]} (snapshot {row[2]}) has no authenticated mail under the failed label to replay"
                : $"snapshot {row[2]} is {row[1]}, and only ok and empty runs are archived to replay"
        );
    var (mail, received) = Archived((string)row[0]);
    previous = Parse(mail).GeneratedAt ?? previous;
    runs.Add((mail, received, previous, false));
}
runs = [.. runs.OrderBy(r => r.At)];
Console.WriteLine($"rewind from snapshot {fromId} ({from.UtcDateTime:u}); replay {runs.Count} runs: {late.Count} late, {runs.Count - late.Count} already loaded");
Console.WriteLine($"through {runs[^1].At.UtcDateTime:u}");
if (!apply)
{
    Console.WriteLine("dry run: pass --apply to rewind and replay");
    return 0;
}

var before = loaded.Where(r => (string)r[1] is "ok" or "empty").ToDictionary(r => (string)r[0], r => Fingerprint((long)r[2], (string)r[3]));
var times = store.Rewind(fromId, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, settings.Tz).DateTime));
foreach (var (mail, received, _, isLate) in runs)
{
    if (!FeedIngest.Ingest(store, mail, received))
        throw new InvalidOperationException($"{mail.MessageId} failed on replay; restore the backup taken before the backfill");
    if (isLate)
        MailArchive.Save(archive, mail.MessageId, received, late.Single(m => m.Mail.MessageId == mail.MessageId).Raw);
}
store.SetIngestedAt(times);

var after = Query("SELECT message_id, id, format FROM snapshots WHERE id >= @id AND status IN ('ok', 'empty')", ("id", fromId))
    .ToDictionary(r => (string)r[0], r => Fingerprint((long)r[1], (string)r[2]));
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
    // Keep the stored id: a mail with no Message-ID was loaded under its IMAP fallback (uid:123).
    return (FeedMail.FromMime(MimeMessage.Load(eml), "archive") with { MessageId = messageId }, received);
}

// A hash of the rows a snapshot reconstructs to, independent of row ids: its blocks, or a legacy
// slots run's own slot rows.
string Fingerprint(long snapshot, string format)
{
    var (rows, from) = format == "blocks"
        ? ($"ROW({FeedStore.BlockColumns})::text", $"blocks WHERE {FeedStore.AtSnapshot}")
        : ("(to_jsonb(slots) - 'snapshot_id')::text", "slots WHERE snapshot_id = @snapshot");
    return (string)Query($"SELECT md5(coalesce(string_agg({rows}, E'\\n' ORDER BY {rows}), '')) FROM {from}", ("snapshot", snapshot))[0][0];
}

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
