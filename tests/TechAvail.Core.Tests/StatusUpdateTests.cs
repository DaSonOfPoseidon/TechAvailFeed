using System.Globalization;
using TechAvail.Core.Parsing;

namespace TechAvail.Core.Tests;

public class StatusUpdateTests
{
    static readonly DateOnly Day = new(2026, 10, 6); // a Tuesday

    static DateTime T(string time) => Day.ToDateTime(TimeOnly.Parse(time, CultureInfo.InvariantCulture));

    static Block Job(
        string reference,
        string start,
        string status = "A",
        string tech = "t1",
        string area = "Hannibal-Bowling Green",
        string kind = "job",
        int hours = 2
    ) =>
        new()
        {
            Kind = kind,
            WorkDate = Day,
            TechId = kind.EndsWith("_unassigned") ? "" : tech,
            TechName = kind.EndsWith("_unassigned") ? "" : tech.ToUpperInvariant(),
            StartsAt = T(start),
            EndsAt = T(start).AddHours(hours),
            RefId = reference,
            Status = status,
            Department = "FIELD",
            Region = area,
            Skills = "",
        };

    static StatusReport Build(string asOf, params Block[] blocks) => StatusUpdate.Build(blocks, Day, T(asOf));

    [Fact]
    public void Areas_map_to_vp_regions_and_unknown_ones_are_unmapped()
    {
        Assert.Equal("STL West", StatusUpdate.RegionFor("Hannibal-Bowling Green"));
        Assert.Equal("STL East", StatusUpdate.RegionFor("Illinois Metro East"));
        Assert.Equal("West", StatusUpdate.RegionFor("Carrollton"));
        Assert.Equal("COMO", StatusUpdate.RegionFor("Columbia Core"));
        Assert.Equal(StatusUpdate.Unmapped, StatusUpdate.RegionFor("Atlantis"));
        Assert.Equal(StatusUpdate.Unmapped, StatusUpdate.RegionFor(""));
        var areas = StatusUpdate.Regions.SelectMany(r => r.Areas).ToList();
        Assert.Equal(areas.Count, areas.Distinct().Count());
    }

    [Theory]
    [InlineData(0, "Green")]
    [InlineData(1, "Yellow")]
    [InlineData(2, "Yellow")]
    [InlineData(3, "Red")]
    public void Status_follows_the_concern_count(int concerns, string status) => Assert.Equal(status, StatusUpdate.Status(concerns));

    [Fact]
    public void A_quiet_day_is_all_green_without_an_unmapped_row()
    {
        var report = Build("10:00", Job("1", "10:00", "I"));
        Assert.Empty(report.Concerns);
        Assert.Equal(["COMO", "STL West", "STL East", "West", "Southwest", "South", "North"], report.Regions.Select(r => r.Region));
        Assert.All(report.Regions, r => Assert.Equal("Green", r.Status));
    }

    [Fact]
    public void A_job_running_long_puts_the_next_one_in_jeopardy_in_one_row()
    {
        // The 8AM job is past its end, the 10AM job hasn't started and is itself in jeopardy at 11:45.
        var report = Build("11:45", Job("1", "08:00", "I"), Job("2", "10:00", "A"));
        var row = Assert.Single(report.Concerns);
        Assert.Equal(("STL West", "Hannibal-Bowling Green", "T1"), (row.Region, row.Area, row.TechName));
        Assert.Equal("8AM job going long – 10AM in jeopardy", row.Reason);
        Assert.Equal("Yellow", report.Regions.Single(r => r.Region == "STL West").Status);
        Assert.Equal(["1", "2"], report.Jeopardy.Select(r => r.Job.RefId));
    }

    [Fact]
    public void Running_long_needs_a_later_job_that_hasnt_started()
    {
        // Past its end but nothing waits on it: only the plain JIJ reason.
        var alone = Build("10:15", Job("1", "08:00", "I"));
        Assert.Equal("8AM job in jeopardy", Assert.Single(alone.Concerns).Reason);
        // The later job is already in progress (the tech swapped order): no running-long reason either.
        var swapped = Build("10:15", Job("1", "08:00", "I"), Job("2", "10:00", "I"));
        Assert.Equal("8AM job in jeopardy", Assert.Single(swapped.Concerns).Reason);
    }

    [Fact]
    public void Running_long_only_threatens_a_next_job_starting_within_the_lead()
    {
        // At 10:15 the 3PM job is hours away: the 8AM one is only in jeopardy.
        var far = Build("10:15", Job("1", "08:00", "I"), Job("2", "15:00", "A"));
        Assert.Equal("8AM job in jeopardy", Assert.Single(far.Concerns).Reason);
        // A later job already in progress: the tech moved on, whatever comes after it.
        var moved = Build("10:15", Job("1", "08:00", "I"), Job("2", "10:00", "C"), Job("3", "10:30", "A", hours: 1));
        Assert.Equal("8AM job in jeopardy", Assert.Single(moved.Concerns).Reason);
        // Due at 10:30, so within the 30-minute lead at 10:15.
        var near = Build("10:15", Job("1", "08:00", "I"), Job("2", "10:30", "A"));
        Assert.Equal("8AM job going long – 10:30AM in jeopardy", Assert.Single(near.Concerns).Reason);
    }

    [Fact]
    public void Repeated_reasons_are_counted()
    {
        var report = Build("16:45", Job("1", "15:00", "O", kind: "ticket"), Job("2", "15:00", "O", kind: "ticket"));
        Assert.Equal("3PM trouble call in jeopardy ×2", Assert.Single(report.Concerns).Reason);
    }

    [Fact]
    public void Several_jobs_in_jeopardy_join_into_one_reason()
    {
        var report = Build("12:00", Job("1", "08:00", "A"), Job("2", "10:00", "O", kind: "ticket"));
        Assert.Equal("8AM job in jeopardy; 10AM trouble call in jeopardy", Assert.Single(report.Concerns).Reason);
    }

    [Fact]
    public void A_slot_with_more_jobs_than_techs_is_flagged_and_counts_unassigned_work()
    {
        var report = Build(
            "09:00",
            Job("1", "10:00", tech: "a", area: "Carrollton"),
            Job("2", "10:00", tech: "b", area: "Carrollton"),
            Job("3", "10:00", area: "Carrollton", kind: "job_unassigned"),
            Job("4", "10:00", tech: "c", area: "Oak Grove-Odessa")
        );
        var row = Assert.Single(report.Concerns);
        Assert.Equal(("West", "Carrollton", (string?)null), (row.Region, row.Area, row.TechName));
        Assert.Equal("10AM slot has 3 jobs but only 2 techs", row.Reason);
    }

    [Fact]
    public void Overbooking_skips_slots_that_are_over_and_closed_work()
    {
        Block[] jobs =
        [
            Job("1", "08:00", "C", tech: "a"),
            Job("2", "08:00", "C", tech: "a"),
            Job("3", "13:00", "A", tech: "b"),
            Job("4", "13:00", "X", tech: "b"),
        ];
        Assert.Empty(Build("10:00", jobs).Concerns);
        var past = Build("10:00", Job("5", "08:00", "C", tech: "a"), Job("6", "08:00", "C", kind: "job_unassigned"));
        Assert.Empty(past.Concerns);
        var none = Build("09:00", Job("7", "15:00", kind: "job_unassigned"));
        Assert.Equal("3PM slot has 1 job but no tech assigned", Assert.Single(none.Concerns).Reason);
    }

    [Fact]
    public void Three_concerns_turn_a_region_red_and_an_unknown_area_gets_its_own_row()
    {
        var report = Build(
            "12:00",
            Job("1", "08:00", tech: "a"),
            Job("2", "08:00", tech: "b"),
            Job("3", "08:00", tech: "c", area: "St Louis West"),
            Job("4", "08:00", tech: "d", area: "Atlantis")
        );
        Assert.Equal(("Red", 3), report.Regions.Where(r => r.Region == "STL West").Select(r => (r.Status, r.Concerns)).Single());
        Assert.Equal(("Unmapped", "Yellow"), (report.Regions[^1].Region, report.Regions[^1].Status));
        Assert.Equal(["Hannibal-Bowling Green", "Hannibal-Bowling Green", "St Louis West", "Atlantis"], report.Concerns.Select(c => c.Area));
    }

    [Fact]
    public void The_run_for_a_time_is_the_first_in_its_quarter_hour_else_the_latest_before()
    {
        Snapshot S(long id, string time) => new(id, T(time));
        Snapshot[] runs = [S(1, "09:45:40"), S(2, "10:00:51"), S(3, "10:15:41")];
        Assert.Equal(2, StatusUpdate.FindAt(runs, Day, new TimeOnly(10, 0))!.Id);
        Assert.Equal(1, StatusUpdate.FindAt([S(1, "09:45:40"), S(3, "10:15:41")], Day, new TimeOnly(10, 0))!.Id);
        Assert.Null(StatusUpdate.FindAt(runs, Day, new TimeOnly(9, 0)));
        Assert.Null(StatusUpdate.FindAt(runs, Day.AddDays(1), new TimeOnly(10, 0)));
    }

    [Fact]
    public void Clock_drops_zero_minutes() =>
        Assert.Equal(["8AM", "10:30AM", "1PM"], new[] { "08:00", "10:30", "13:00" }.Select(t => StatusUpdate.Clock(T(t))));
}
