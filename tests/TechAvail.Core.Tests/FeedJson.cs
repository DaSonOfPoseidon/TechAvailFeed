using System.Globalization;
using System.Text.Json.Nodes;
using TechAvail.Core;
using TechAvail.Core.Parsing;

namespace TechAvail.Core.Tests;

// What the parser makes of a feed file, as JSON: the rows with every field, or the error message.
public static class FeedJson
{
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
            ["set_region"] = b.SetRegion,
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

    // Feed timestamps are local and carry no seconds fraction.
    static string? Local(DateTime? value) =>
        value?.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
}
