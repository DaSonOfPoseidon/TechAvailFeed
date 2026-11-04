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
        bool? prereqs = null,
        string? reason = null
    ) =>
        new(kind, reference, tech, tech.ToUpperInvariant(), new DateTime(2026, 10, 6, 9, 0, 0))
        {
            Outcomes = new(outcomes),
            Reached = reached,
            PrereqsOpen = prereqs,
            Region = region,
            IncompleteReason = reason,
        };

    static JsonNode Kpis(List<(DayOutcome, bool)> days, string? region = null, string? tech = null) =>
        JsonSerializer.SerializeToNode(Api.Kpis.OutcomeKpis(days, region, tech), Json)!;

    [Fact]
    public void Only_an_empty_sunday_is_left_off_the_charts()
    {
        var (saturday, sunday) = (new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 11));
        Assert.True(Api.Kpis.Charted(saturday, empty: true));
        Assert.False(Api.Kpis.Charted(sunday, empty: true));
        Assert.True(Api.Kpis.Charted(sunday, empty: false));
    }

    [Fact]
    public void A_sunday_without_a_morning_plan_is_charted()
    {
        var sunday = new DateOnly(2026, 10, 11);
        var empty = new DayOutcome(sunday, "ok", new Snapshot(1, sunday.ToDateTime(new TimeOnly(6, 0))), []);
        var days = Api.Kpis.OutcomeKpis([(empty, false), (new DayOutcome(sunday, "no_morning"), false)]).Days;
        Assert.False(Api.Kpis.Charted(days[0]));
        Assert.True(Api.Kpis.Charted(days[1]));
    }

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
                Item("3", new() { ["d0"] = "canceled", ["d1"] = "canceled" }, reached: "in_progress", prereqs: true, reason: "SX_NOCONTACT"),
                Item("4", new() { ["d0"] = "rescheduled" }, tech: "b", region: "South", reason: "CR_REQUEST"),
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
        Assert.Equal((1, 1, 0), ((int)pulled["customer"]!, (int)pulled["socket"]!, (int)pulled["no_reason"]!));
        Assert.Equal((1, 1), ((int)pulled["by_reason"]!["SX_NOCONTACT"]!, (int)pulled["by_reason"]!["CR_REQUEST"]!));
        Assert.Equal(0.5, (double)job["pulled_d0_rate"]!);
        Assert.Equal((0.25, 0.25, 0), ((double)job["pulled_customer_rate"]!, (double)job["pulled_socket_rate"]!, (double)job["pulled_no_reason_rate"]!));
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

    [Fact]
    public void Grouped_outcomes_combine_a_renamed_territory_and_mbs_rows_keep_both_names()
    {
        var day = new DayOutcome(
            Day,
            "ok",
            Morning,
            [
                Item("1", new() { ["d0"] = "completed" }, region: "Lamar-Webb City"),
                Item("2", new() { ["d0"] = "open" }, tech: "b", region: "Nevada-Lamar"),
            ]
        );
        static List<(string Region, int Planned)> ByRegion(JsonNode result) =>
            [.. result["by_region"]!.AsArray().Select(r => ((string)r!["region"]!, (int)r["job"]!["planned"]!))];

        var perTerritory = JsonSerializer.SerializeToNode(Api.Kpis.OutcomeKpis([(day, false)]), Json)!;
        Assert.Equal([("Lamar-Webb City", 1), ("Nevada-Lamar", 1)], ByRegion(perTerritory));

        var grouped = JsonSerializer.SerializeToNode(Api.Kpis.OutcomeKpis([(day, false)], grouped: true), Json)!;
        Assert.Equal([("Nevada-Lamar", 2)], ByRegion(grouped));
        Assert.Equal(0.5, (double)grouped["by_region"]![0]!["job"]!["completion_rate"]!["d0"]!);

        var filtered = JsonSerializer.SerializeToNode(Api.Kpis.OutcomeKpis([(day, false)], "Nevada-Lamar", grouped: true), Json)!;
        Assert.Equal(2, (int)filtered["totals"]!["job"]!["planned"]!);
    }

    [Fact]
    public void Grouped_outcomes_sum_the_territories_counts_and_an_area_filter_takes_them_all()
    {
        var day = new DayOutcome(
            Day,
            "ok",
            Morning,
            [
                Item("1", new() { ["d0"] = "completed" }, region: "Columbia Core"),
                Item("2", new() { ["d0"] = "open" }, tech: "b", region: "Lake Ozark Area"),
                Item("3", new() { ["d0"] = "completed" }, tech: "c", region: "Carrollton"),
            ]
        );
        static List<(string Region, int Planned)> ByRegion(JsonNode result) =>
            [.. result["by_region"]!.AsArray().Select(r => ((string)r!["region"]!, (int)r["job"]!["planned"]!))];

        var perTerritory = JsonSerializer.SerializeToNode(Api.Kpis.OutcomeKpis([(day, false)]), Json)!;
        Assert.Equal([("Carrollton", 1), ("Columbia Core", 1), ("Lake Ozark Area", 1)], ByRegion(perTerritory));

        var grouped = JsonSerializer.SerializeToNode(Api.Kpis.OutcomeKpis([(day, false)], grouped: true), Json)!;
        Assert.Equal([("Carrollton", 1), ("Central MO", 2)], ByRegion(grouped));
        Assert.Equal(1, (int)grouped["by_region"]![1]!["job"]!["completed"]!["d0"]!);
        Assert.Equal(0.5, (double)grouped["by_region"]![1]!["job"]!["completion_rate"]!["d0"]!);

        var central = JsonSerializer.SerializeToNode(Api.Kpis.OutcomeKpis([(day, false)], "Central MO", grouped: true), Json)!;
        Assert.Equal(2, (int)central["totals"]!["job"]!["planned"]!);
        Assert.Equal([("Central MO", 2)], ByRegion(central));
        Assert.Equal(["a", "b"], central["techs"]!.AsArray().Select(t => (string)t!["tech_id"]!));

        var one = JsonSerializer.SerializeToNode(Api.Kpis.OutcomeKpis([(day, false)], "Columbia Core"), Json)!;
        Assert.Equal(1, (int)one["totals"]!["job"]!["planned"]!);

        var withTech = JsonSerializer.SerializeToNode(Api.Kpis.OutcomeKpis([(day, false)], "Central MO", "b"), Json)!;
        Assert.Equal(1, (int)withTech["totals"]!["job"]!["planned"]!);
        Assert.Equal(0, (int)withTech["totals"]!["job"]!["completed"]!["d0"]!);
        Assert.Equal(0, (int)JsonSerializer.SerializeToNode(Api.Kpis.OutcomeKpis([(day, false)], "Nowhere"), Json)!["totals"]!["job"]!["planned"]!);
    }

    [Fact]
    public void Techs_list_the_range_ignoring_the_tech_filter()
    {
        var day = new DayOutcome(
            Day,
            "ok",
            Morning,
            [Item("1", new() { ["d0"] = "completed" }), Item("2", new() { ["d0"] = "open" }, tech: "b", region: "South")]
        );
        var techs = Kpis([(day, false)], tech: "a")["techs"]!.AsArray();
        Assert.Equal(["a", "b"], techs.Select(t => (string)t!["tech_id"]!));
        Assert.Equal("B", (string)techs[1]!["tech_name"]!);
        Assert.Equal(["b"], Kpis([(day, false)], region: "South", tech: "a")["techs"]!.AsArray().Select(t => (string)t!["tech_id"]!));
    }
}
