using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MimeKit;
using TechAvail.Core;
using TechAvail.Core.Mail;
using TechAvail.Core.Parsing;

namespace TechAvail.Parity;

// The JSON shape tools/golden.py writes for the Python pipeline, built from the .NET one, and a
// diff that reports only where two documents differ (paths, never values: corpus values are real).
public static class GoldenJson
{
    // tools/golden.py's mail_json.
    public static JsonObject Mail(byte[] raw, string mailFrom, string authservId)
    {
        var message = MimeMessage.Load(new MemoryStream(raw));
        var mail = FeedMail.FromMime(message, "0");
        var attachments = new JsonArray();
        foreach (var (name, data) in mail.Attachments)
            attachments.Add(new JsonObject { ["filename"] = name, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(data)) });
        var result = new JsonObject
        {
            ["message_id"] = mail.MessageId,
            ["subject"] = mail.Subject,
            ["email_date"] = mail.EmailDate?.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
            ["sender_rejection"] = SenderCheck.Rejection(message, mailFrom, authservId),
            ["attachments"] = attachments,
        };
        if (mail.Attachments.Count == 1)
        {
            var data = mail.Attachments[0].Data;
            result["feed"] = Feed(data);
            if (result["feed"]!["format"]?.GetValue<string>() == "blocks" && result["feed"]!["generated_at"] is not null)
                result["availability"] = Availability(FeedParser.Parse(data));
        }
        return result;
    }

    // tools/golden.py's availability_json: as the dashboard would compute it when the snapshot was
    // generated, "now" being its local wall-clock time.
    public static JsonObject Availability(ParsedFeed feed)
    {
        var now = feed.GeneratedAt!.Value.DateTime;
        var result = new JsonObject
        {
            ["now"] = Local(now),
            ["unassigned"] = new JsonArray(
                [
                    .. Core.Availability.Unassigned(feed.Blocks, DateOnly.FromDateTime(now))
                        .Select(d => (JsonNode)new JsonObject
                        {
                            ["work_date"] = Date(d.WorkDate),
                            ["region"] = d.Region,
                            ["jobs"] = d.Jobs,
                            ["tickets"] = d.Tickets,
                            ["hours"] = d.Hours,
                        }),
                ]
            ),
        };
        foreach (var calendar in new[] { "install", "tc" })
        {
            result[calendar] = new JsonObject
            {
                ["free_slots"] = new JsonArray(
                    [.. Core.Availability.FreeSlots(feed.Blocks, now, calendar: calendar).Select(FreeSlotJson)]
                ),
                ["tech_days"] = new JsonArray(
                    [.. Core.Availability.TechDays(feed.Blocks, now, calendar: calendar).Select(TechDayJson)]
                ),
            };
        }
        return result;
    }

    static JsonNode FreeSlotJson(FreeSlot s) =>
        new JsonObject
        {
            ["work_date"] = Date(s.WorkDate),
            ["tech_id"] = s.TechId,
            ["tech_name"] = s.TechName,
            ["open_from"] = Local(s.OpenFrom),
            ["open_until"] = Local(s.OpenUntil),
            ["open_minutes"] = s.OpenMinutes,
            ["region"] = s.Region,
            ["skills"] = s.Skills,
        };

    static JsonArray Intervals(IEnumerable<(DateTime Start, DateTime End)> intervals) =>
        new([.. intervals.Select(i => (JsonNode)new JsonArray(Local(i.Start), Local(i.End)))]);

    static JsonNode TechDayJson(TechDay d) =>
        new JsonObject
        {
            ["work_date"] = Date(d.WorkDate),
            ["tech_id"] = d.TechId,
            ["tech_name"] = d.TechName,
            ["region"] = d.Region,
            ["skills"] = d.Skills,
            ["shifts"] = Intervals(d.Shifts),
            ["time_off"] = Intervals(d.TimeOff),
            ["work"] = new JsonArray(
                [.. d.Work.Select(b => (JsonNode)new JsonArray(b.Kind, b.RefId, Local(b.StartsAt), Local(b.EndsAt)))]
            ),
            ["free"] = new JsonArray([.. d.Free.Select(FreeSlotJson)]),
            ["shift_hours"] = d.ShiftHours,
            ["lunch_hours"] = d.LunchHours,
            ["time_off_hours"] = d.TimeOffHours,
            ["available_hours"] = d.AvailableHours,
            ["booked_hours"] = d.BookedHours,
            ["free_hours"] = d.FreeHours,
            ["jobs"] = d.Jobs,
            ["tickets"] = d.Tickets,
            ["on_time_off"] = d.OnTimeOff,
        };

    public static JsonObject Feed(byte[] data)
    {
        ParsedFeed feed;
        try
        {
            feed = FeedParser.Parse(data);
        }
        catch (FeedParseException exc)
        {
            return new JsonObject { ["error"] = exc.Message };
        }
        var rows = new JsonArray();
        if (feed.Format == "blocks")
            foreach (var block in feed.Blocks)
                rows.Add(Block(block));
        else
            foreach (var slot in feed.Slots)
                rows.Add(Slot(slot));
        return new JsonObject
        {
            ["sha256"] = feed.Sha256,
            ["format"] = feed.Format,
            ["generated_at"] = feed.GeneratedAt?.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
            ["row_count"] = feed.RowCount,
            ["rows"] = rows,
        };
    }

    static JsonObject Block(Block b) =>
        new()
        {
            ["kind"] = b.Kind,
            ["work_date"] = Date(b.WorkDate),
            ["tech_id"] = b.TechId,
            ["tech_name"] = b.TechName,
            ["starts_at"] = Local(b.StartsAt),
            ["ends_at"] = Local(b.EndsAt),
            ["ref_id"] = b.RefId,
            ["status"] = b.Status,
            ["department"] = b.Department,
            ["region"] = b.Region,
            ["skills"] = b.Skills,
            ["task_type"] = b.TaskType,
            ["modified_at"] = Local(b.ModifiedAt),
            ["modified_by"] = b.ModifiedBy,
            ["enroute_at"] = Local(b.EnrouteAt),
            ["inprogress_at"] = Local(b.InprogressAt),
            ["prereqs_status"] = b.PrereqsStatus,
            ["address_issue"] = b.AddressIssue,
            ["latitude"] = b.Latitude,
            ["longitude"] = b.Longitude,
            ["gps_precision"] = b.GpsPrecision,
        };

    static JsonObject Slot(Slot s) =>
        new()
        {
            ["work_date"] = Date(s.WorkDate),
            ["tech_id"] = s.TechId,
            ["tech_name"] = s.TechName,
            ["open_from"] = Local(s.OpenFrom),
            ["open_until"] = Local(s.OpenUntil),
            ["open_minutes"] = s.OpenMinutes,
            ["region"] = s.Region,
            ["skills"] = s.Skills,
        };

    static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // Python's isoformat() of a naive datetime (feed timestamps carry no seconds fraction).
    static string? Local(DateTime? value) =>
        value?.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    public static List<string> Diff(JsonNode? expected, JsonNode? actual, string path = "")
    {
        var diffs = new List<string>();
        // Through text, so nodes built in memory (holding CLR ints, doubles) compare like parsed ones.
        Walk(Reparse(expected), Reparse(actual), path, diffs);
        return diffs;
    }

    static JsonNode? Reparse(JsonNode? node) => node is null ? null : JsonNode.Parse(node.ToJsonString());

    static void Walk(JsonNode? expected, JsonNode? actual, string path, List<string> diffs)
    {
        switch (expected, actual)
        {
            case (null, null):
                return;
            case (JsonObject e, JsonObject a):
                foreach (var key in e.Select(p => p.Key).Union(a.Select(p => p.Key)).Order(StringComparer.Ordinal))
                {
                    var child = path.Length == 0 ? key : $"{path}.{key}";
                    if (!e.ContainsKey(key) || !a.ContainsKey(key))
                        diffs.Add($"{child} (missing on one side)");
                    else
                        Walk(e[key], a[key], child, diffs);
                }
                return;
            case (JsonArray e, JsonArray a):
                if (e.Count != a.Count)
                    diffs.Add($"{path} (length {e.Count} vs {a.Count})");
                for (int i = 0; i < Math.Min(e.Count, a.Count); i++)
                    Walk(e[i], a[i], $"{path}[{i}]", diffs);
                return;
            case (JsonValue e, JsonValue a) when SameValue(e, a, path):
                return;
            default:
                diffs.Add(path);
                return;
        }
    }

    static bool SameValue(JsonValue expected, JsonValue actual, string path)
    {
        if (expected.GetValueKind() != actual.GetValueKind())
            return false;
        if (expected.GetValueKind() == System.Text.Json.JsonValueKind.Number)
            return expected.GetValue<double>() == actual.GetValue<double>();
        if (expected.GetValueKind() != System.Text.Json.JsonValueKind.String)
            return expected.ToJsonString() == actual.ToJsonString();
        var (e, a) = (expected.GetValue<string>(), actual.GetValue<string>());
        // Python keeps a timestamp without an offset naive, .NET reads it as UTC (as Postgres
        // stores it), so the same instant is what has to match.
        if (path.EndsWith("generated_at", StringComparison.Ordinal) || path == "email_date")
            return Instant(e) == Instant(a);
        return e == a;
    }

    static DateTimeOffset? Instant(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
}
