using TechAvail.Core;
using TechAvail.Core.Parsing;

namespace TechAvail.Api.Tests;

public class CapacityTests
{
    static readonly DateOnly Mon = new(2026, 10, 5);
    static readonly DateOnly Tue = new(2026, 10, 6);
    static readonly DateTime Early = new(2026, 10, 1, 7, 0, 0);

    static Block B(
        string kind,
        DateOnly day,
        string start,
        string end,
        string tech = "a",
        string region = "",
        string skills = "",
        string status = "A",
        string reference = "",
        string department = "FIELD"
    ) =>
        new()
        {
            Kind = kind,
            WorkDate = day,
            TechId = tech,
            TechName = tech.ToUpperInvariant(),
            StartsAt = day.ToDateTime(TimeOnly.Parse(start)),
            EndsAt = day.ToDateTime(TimeOnly.Parse(end)),
            RefId = reference,
            Status = status,
            Department = department,
            Region = region,
            Skills = skills,
        };

    static readonly List<Block> Blocks =
    [
        B("shift", Mon, "08:00", "17:00", "a", "North", "INS, RECO"),
        B("shift", Mon, "08:00", "17:00", "b", "South", "INS"),
        B("job", Mon, "08:00", "10:00", "a", reference: "j1"),
        B("ticket", Mon, "13:00", "14:00", "b", reference: "t1"),
        B("time_off", Tue, "00:00", "23:59", "a"),
        B("job_unassigned", Mon, "09:00", "11:00", "", "North", reference: "u1"),
        B("ticket_unassigned", Mon, "09:00", "10:00", "", "South", reference: "u2"),
        B("job_unassigned", Mon, "09:00", "11:00", "", "North", status: "X", reference: "u3"),
    ];

    static List<CalendarEntry> Build(string? region = null, string? skill = null)
    {
        var days = CapacityRollup.FilterDays(Availability.TechDays(Blocks, Early, Mon, Tue), region, skill);
        return CapacityRollup.Entries(days, CapacityRollup.UnassignedWork(Blocks, Mon, Tue, region), Mon, Tue);
    }

    [Fact]
    public void Calendar_totals_and_regions()
    {
        var (monday, tuesday) = (Build()[0], Build()[1]);
        var t = monday.Totals;
        Assert.Equal((2, 18.0, 16.0), (t.TechsOn, t.ShiftH, t.AvailableH));
        Assert.Equal((3.0, 1, 1), (t.BookedH, t.Jobs, t.Tickets));
        Assert.Equal(13, t.FreeH);
        Assert.Equal((1, 1), (t.UnassignedJobs, t.UnassignedTickets));
        Assert.Equal((3.0, 10.0), (t.UnassignedH, t.NetH));
        Assert.Equal(Math.Round(3.0 / 16, 3), t.Utilization);
        Assert.Equal(["North", "South"], monday.ByRegion.Select(r => r.Region));
        // 10-12 and 13-17, minus u1.
        Assert.Equal(6 - 2, monday.ByRegion[0].NetH);
        // Tech a is off all of Tuesday; their usual region still gets the head count.
        Assert.Equal(1, tuesday.Totals.TechsOff);
        Assert.Null(tuesday.Totals.Utilization);
        Assert.Equal("North", tuesday.ByRegion[0].Region);
    }

    [Fact]
    public void Every_date_in_range_gets_an_entry()
    {
        var entries = CapacityRollup.Entries([], [], Mon, new DateOnly(2026, 10, 9));
        Assert.Equal(5, entries.Count);
        Assert.Equal(0, entries[^1].Totals.TechsOn);
    }

    [Fact]
    public void Region_filter_applies_to_techs_and_demand()
    {
        var totals = Build(region: "South")[0].Totals;
        Assert.Equal((1, 0, 1), (totals.TechsOn, totals.UnassignedJobs, totals.UnassignedTickets));
    }

    [Fact]
    public void Skill_filter_matches_whole_codes()
    {
        Assert.Equal(1, Build(skill: "reco")[0].Totals.TechsOn);
        Assert.Equal(0, Build(skill: "REC")[0].Totals.TechsOn);
    }

    [Fact]
    public void Region_totals_sum_the_range()
    {
        var north = CapacityRollup.RegionTotals(Build())[0];
        Assert.Equal(("North", 1, 2.0), (north.Region, north.TechDaysOff, north.UnassignedH));
    }

    [Fact]
    public void Unassigned_tc_work_is_tc_demand()
    {
        List<Block> blocks = [.. Blocks, B("ticket_unassigned", Mon, "09:00", "10:00", "", "North", reference: "u9", department: "TC")];
        Assert.DoesNotContain("u9", CapacityRollup.UnassignedWork(blocks, Mon, Tue).Select(b => b.RefId));
        Assert.Equal(["u9"], CapacityRollup.UnassignedWork(blocks, Mon, Tue, calendar: "tc").Select(b => b.RefId));
    }
}
