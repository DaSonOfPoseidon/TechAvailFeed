using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TechAvail.Core;
using TechAvail.Data;

namespace TechAvail.Parity;

// The .NET side of tools/replay.py's finalize and history commands, in the same JSON shape.
public static class HistoryDump
{
    static TimeZoneInfo Tz =>
        TimeZoneInfo.FindSystemTimeZoneById(Environment.GetEnvironmentVariable("MAIL_TZ") ?? "America/Chicago");

    public static int Finalize(string connectionString)
    {
        Console.WriteLine($"{new OutcomeHistory(new FeedStore(connectionString), Tz).Finalize()} days finalized");
        return 0;
    }

    public static int Write(string connectionString, string start, string end, string output)
    {
        var history = new OutcomeHistory(new FeedStore(connectionString), Tz);
        var days = new JsonArray();
        foreach (var (outcome, provisional) in history.Range(DateOnly.Parse(start, CultureInfo.InvariantCulture), DateOnly.Parse(end, CultureInfo.InvariantCulture)))
        {
            days.Add(
                new JsonObject
                {
                    ["day"] = outcome.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["status"] = outcome.Status,
                    ["provisional"] = provisional,
                    ["morning"] = outcome.Morning is { } m ? new JsonArray(m.Id, Local(m.At)) : null,
                    ["items"] = new JsonArray([.. outcome.Items.Select(Item)]),
                    ["added_after_morning"] = JsonSerializer.SerializeToNode(outcome.AddedAfterMorning),
                    ["summary"] = outcome.Status == "ok" ? JsonSerializer.SerializeToNode(Outcomes.Summarise(outcome)) : null,
                }
            );
        }
        File.WriteAllText(output, days.ToJsonString());
        Console.WriteLine($"{days.Count} days");
        return 0;
    }

    static JsonNode Item(Planned p) =>
        new JsonObject
        {
            ["kind"] = p.Kind,
            ["ref_id"] = p.RefId,
            ["tech_id"] = p.TechId,
            ["tech_name"] = p.TechName,
            ["planned_start"] = Local(p.PlannedStart),
            ["outcomes"] = JsonSerializer.SerializeToNode(p.Outcomes),
            ["reached"] = p.Reached,
            ["prereqs_open"] = p.PrereqsOpen,
            ["region"] = p.Region,
            ["task_type"] = p.TaskType,
        };

    // Python's isoformat(): microseconds only when there are any.
    static string Local(DateTime value) =>
        value.ToString(value.Ticks % TimeSpan.TicksPerSecond == 0 ? "yyyy-MM-dd'T'HH:mm:ss" : "yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
}
