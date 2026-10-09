using System.Text.Json;
using System.Text.Json.Nodes;
using TechAvail.Core;

namespace TechAvail.Api.Tests;

// Asserts on the JSON the API serves, so the shape is checked along with the numbers.
public class KpisTests
{
    static readonly DateOnly Day = new(2026, 10, 6);
    static readonly Snapshot Morning = new(1, new DateTime(2026, 10, 6, 6, 0, 0));
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    static Planned Item(
        string reference,
        Dictionary<string, string> outcomes,
        string kind = "job",
        string tech = "a",
        string region = "North",
        string reached = "not_started",
        bool? prereqs = null
    ) =>
        new(kind, reference, tech, tech.ToUpperInvariant(), new DateTime(2026, 10, 6, 9, 0, 0))
        {
            Outcomes = new(outcomes),
            Reached = reached,
            PrereqsOpen = prereqs,
            Region = region,
        };

    static JsonNode Kpis(List<(DayOutcome, bool)> days, string? region = null, string? tech = null) =>
        JsonSerializer.SerializeToNode(Api.Kpis.OutcomeKpis(days, region, tech), Json)!;

    [Fact]
    public void Rates_use_the_planned_count_and_the_latest_checkpoint()
    {
        var day = new DayOutcome(
            Day,
            "ok",
            Morning,
            [
                Item("1", new() { ["d0"] = "completed", ["d1"] = "completed", ["d2"] = "completed" }),
                // Provisional: no d2 yet.
                Item("2", new() { ["d0"] = "open", ["d1"] = "completed" }),
                Item("3", new() { ["d0"] = "canceled", ["d1"] = "canceled" }, reached: "in_progress", prereqs: true),
                Item("4", new() { ["d0"] = "rescheduled" }, tech: "b", region: "South"),
                Item("T1", new() { ["d0"] = "completed" }, kind: "ticket"),
            ]
        );
        var result = Kpis([(day, true)]);
        var job = result["totals"]!["job"]!;
        Assert.Equal(4, (int)job["planned"]!);
        Assert.Equal((1, 2, 1), ((int)job["completed"]!["d0"]!, (int)job["completed"]!["d1"]!, (int)job["completed"]!["d2"]!));
        Assert.Equal(0.25, (double)job["completion_rate"]!["d0"]!);
        Assert.Equal((2, 1), ((int)job["outcome"]!["completed"]!, (int)job["outcome"]!["canceled"]!));
        Assert.Equal(0.25, (double)job["outcome_rate"]!["rescheduled"]!);
        var pulled = job["pulled_d0"]!;
        Assert.Equal((2, 1, 1), ((int)pulled["total"]!, (int)pulled["in_progress"]!, (int)pulled["prereqs_open"]!));
        Assert.Equal(0.5, (double)job["pulled_d0_rate"]!);
        Assert.Equal(1, (double)result["totals"]!["ticket"]!["completion_rate"]!["d0"]!);
        Assert.True((bool)result["days"]![0]!["provisional"]!);
        Assert.Equal(["North", "South"], result["by_region"]!.AsArray().Select(r => (string)r!["region"]!));
        Assert.Equal(["a", "b"], result["by_tech"]!.AsArray().Select(t => (string)t!["tech_id"]!));
    }

    [Fact]
    public void No_morning_days_are_listed_but_not_counted()
    {
        var result = Kpis([(new DayOutcome(new DateOnly(2026, 10, 5), "no_morning"), false)]);
        var day = result["days"]![0]!.AsObject();
        Assert.Equal(["date", "status", "provisional"], day.Select(p => p.Key));
        Assert.Equal(("2026-10-05", "no_morning", false), ((string)day["date"]!, (string)day["status"]!, (bool)day["provisional"]!));
        Assert.Equal(0, (int)result["totals"]!["job"]!["planned"]!);
        Assert.Null(result["totals"]!["job"]!["completion_rate"]!["d0"]);
    }

    [Fact]
    public void Filters_narrow_to_one_region_or_tech()
    {
        var day = new DayOutcome(
            Day,
            "ok",
            Morning,
            [Item("1", new() { ["d0"] = "completed" }), Item("2", new() { ["d0"] = "open" }, tech: "b", region: "South")]
        );
        Assert.Equal(1, (int)Kpis([(day, false)], region: "South")["totals"]!["job"]!["planned"]!);
        Assert.Equal(1, (int)Kpis([(day, false)], tech: "a")["totals"]!["job"]!["outcome"]!["completed"]!);
    }
}
