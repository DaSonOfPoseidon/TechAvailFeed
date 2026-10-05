using System.Globalization;
using TechAvail.Core.Parsing;

namespace TechAvail.Core.Tests;

// Port of tests/test_outcomes.py.
public class OutcomesTests
{
    static readonly DateOnly Day = new(2026, 10, 6);

    static DateTime T(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture);

    static Snapshot Snap(long id, string when) => new(id, T(when));

    static Block Work(
        string reference,
        string status = "A",
        string start = "2026-10-06 09:00",
        string kind = "job",
        string tech = "t1",
        string department = "FIELD",
        bool traced = false,
        string? enroute = null,
        string? inprogress = null,
        string prebury = ""
    )
    {
        var startsAt = T(start);
        return new Block
        {
            Kind = kind,
            WorkDate = DateOnly.FromDateTime(startsAt),
            TechId = tech,
            TechName = tech.ToUpperInvariant(),
            StartsAt = startsAt,
            EndsAt = startsAt,
            RefId = reference,
            Status = status,
            Department = department,
            Region = "",
            Skills = "",
            ModifiedAt = traced ? startsAt : null,
            EnrouteAt = enroute is null ? null : T(enroute),
            InprogressAt = inprogress is null ? null : T(inprogress),
            PrereqsStatus = prebury,
        };
    }

    static Dictionary<string, IReadOnlyList<Block>?> Checkpoints(params (string, Block[]?)[] items) =>
        items.ToDictionary(i => i.Item1, i => (IReadOnlyList<Block>?)i.Item2);

    static OrderedDictionary<string, object> Job(OrderedDictionary<string, object> summary) =>
        (OrderedDictionary<string, object>)summary["job"];

    [Fact]
    public void Morning_is_the_first_snapshot_between_six_and_seven()
    {
        Snapshot[] snaps =
        [
            Snap(1, "2026-10-06 05:45"),
            Snap(2, "2026-10-06 06:15"),
            Snap(3, "2026-10-06 06:30"),
            Snap(4, "2026-10-06 07:00"),
        ];
        Assert.Equal(2, Outcomes.FindMorning(snaps, Day)!.Id);
    }

    [Fact]
    public void No_snapshot_in_the_morning_window_is_not_guessed()
    {
        Assert.Null(Outcomes.FindMorning([Snap(1, "2026-10-06 05:45"), Snap(2, "2026-10-06 07:15")], Day));
        Assert.Equal("no_morning", Outcomes.DayOutcome(Day, null, [], Checkpoints()).Status);
    }

    [Fact]
    public void Checkpoints_are_the_last_snapshot_before_each_midnight()
    {
        var morning = Snap(1, "2026-10-06 06:00");
        Snapshot[] snaps = [morning, Snap(2, "2026-10-06 23:45"), Snap(3, "2026-10-07 00:00"), Snap(4, "2026-10-07 23:45")];
        Assert.Equal(2, Outcomes.FindCheckpoint(snaps, morning, Day, 0)!.Id);
        Assert.Equal(4, Outcomes.FindCheckpoint(snaps, morning, Day, 1)!.Id);
        // d2 hasn't ended yet: the latest snapshot is the outcome so far.
        Assert.Equal(4, Outcomes.FindCheckpoint(snaps, morning, Day, 2)!.Id);
    }

    [Fact]
    public void Classification_order()
    {
        Assert.Equal("missing", Outcomes.Classify("job", Day, null));
        Assert.Equal("completed", Outcomes.Classify("job", Day, Work("1", "C")));
        Assert.Equal("completed", Outcomes.Classify("ticket", Day, Work("1", "R", kind: "ticket")));
        Assert.Equal("canceled", Outcomes.Classify("job", Day, Work("1", "X")));
        Assert.Equal("canceled", Outcomes.Classify("ticket", Day, Work("1", "D", kind: "ticket")));
        Assert.Equal("handed_off", Outcomes.Classify("job", Day, Work("1", department: "FLDSVCCON")));
        var moved = Work("1", start: "9999-12-31 00:00", kind: "job_moved", tech: "");
        Assert.Equal("unscheduled", Outcomes.Classify("job", Day, moved));
        Assert.Equal("rescheduled", Outcomes.Classify("job", Day, Work("1", start: "2026-11-15 09:00")));
        Assert.Equal("unassigned", Outcomes.Classify("job", Day, Work("1", tech: "")));
        Assert.Equal("open", Outcomes.Classify("job", Day, Work("1", "I")));
    }

    [Fact]
    public void A_completed_job_beats_a_moved_date() =>
        Assert.Equal("completed", Outcomes.Classify("job", Day, Work("1", "C", start: "2026-10-07 09:00")));

    [Fact]
    public void First_release_rows_without_department_are_not_handed_off() =>
        Assert.Equal("open", Outcomes.Classify("job", Day, Work("1", department: "")));

    [Fact]
    public void Day_outcome_counts_each_job_once_and_tracks_checkpoints()
    {
        Block[] morning =
        [
            Work("1", tech: "a"),
            Work("1", tech: "b"), // two techs on one job
            Work("2"),
            Work("3", "X"), // already canceled at 06:00: not planned
            Work("4", start: "2026-10-07 09:00"), // tomorrow's job
            Work("T1", kind: "ticket"),
        ];
        Block[] d0 =
        [
            Work("1", "C", tech: "a"),
            Work("1", "C", tech: "b"),
            Work("2", "I"),
            Work("5"), // booked after the morning
            Work("T1", "C", kind: "ticket"),
        ];
        Block[] d1 = [Work("1", "C"), Work("2", "C"), Work("T1", "C", kind: "ticket")];
        var outcome = Outcomes.DayOutcome(
            Day,
            Snap(1, "2026-10-06 06:00"),
            morning,
            Checkpoints(("d0", d0), ("d1", d1), ("d2", d1))
        );
        var summary = Outcomes.Summarise(outcome);
        var job = Job(summary);
        var ticket = (OrderedDictionary<string, object>)summary["ticket"];
        Assert.Equal((2, 1, 2, 2, 1), ((int)job["planned"], (int)job["completed_d0"], (int)job["completed_d1"], (int)job["completed_d2"], (int)job["added_after_morning"]));
        Assert.Equal((1, 1), ((int)ticket["planned"], (int)ticket["completed_d0"]));
    }

    [Fact]
    public void Job_that_leaves_the_feed_is_missing_and_moved_rows_are_found()
    {
        Block[] d0 = [Work("2", start: "9999-12-31 00:00", kind: "job_moved", tech: "")];
        var outcome = Outcomes.DayOutcome(
            Day,
            Snap(1, "2026-10-06 06:00"),
            [Work("1"), Work("2")],
            Checkpoints(("d0", d0), ("d1", null))
        );
        var byRef = outcome.Items.ToDictionary(i => i.RefId, i => i.Outcomes);
        Assert.Equal([KeyValuePair.Create("d0", "missing")], byRef["1"]);
        Assert.Equal([KeyValuePair.Create("d0", "unscheduled")], byRef["2"]);
    }

    [Fact]
    public void Only_install_types_are_planned_but_untyped_rows_count()
    {
        var install = Work("1") with { TaskType = "3" };
        var other = Work("2") with { TaskType = "14" }; // Drop/Bury: blocks time, not part of the history
        var untyped = Work("3"); // from a feed version without task_type
        var outcome = Outcomes.DayOutcome(Day, Snap(1, "2026-10-06 06:00"), [install, other, untyped], Checkpoints());
        Assert.Equal(["1", "3"], outcome.Items.Select(i => i.RefId).Order());
    }

    [Fact]
    public void Reached_is_the_furthest_stage_on_the_plan_day()
    {
        Assert.Equal("unknown", Outcomes.Reached(Day, null));
        Assert.Equal("unknown", Outcomes.Reached(Day, Work("1"))); // snapshot from before the trace columns
        Assert.Equal("not_started", Outcomes.Reached(Day, Work("1", traced: true)));
        Assert.Equal("en_route", Outcomes.Reached(Day, Work("1", traced: true, enroute: "2026-10-06 08:10")));
        var both = Work("1", traced: true, enroute: "2026-10-06 08:10", inprogress: "2026-10-06 09:02");
        Assert.Equal("in_progress", Outcomes.Reached(Day, both));
        // In Progress on an earlier attempt says nothing about this day.
        Assert.Equal("not_started", Outcomes.Reached(Day, Work("1", traced: true, inprogress: "2026-10-05 09:00")));
    }

    [Fact]
    public void Prereqs_are_open_until_completed_or_dropped()
    {
        Assert.Null(Outcomes.PrereqsOpen(Work("1")));
        Assert.False(Outcomes.PrereqsOpen(Work("1", traced: true)));
        Assert.True(Outcomes.PrereqsOpen(Work("1", traced: true, prebury: "PreBury - Connectorized: A")));
        Assert.True(Outcomes.PrereqsOpen(Work("1", traced: true, prebury: "PreBury - Legacy: W")));
        Assert.False(Outcomes.PrereqsOpen(Work("1", traced: true, prebury: "PreBury - Legacy: C")));
        Assert.False(Outcomes.PrereqsOpen(Work("1", traced: true, prebury: "PreBury - Legacy: X")));
        // Legacy orders: a finished pre-drop doesn't hide an open pre-bury.
        Assert.True(Outcomes.PrereqsOpen(Work("1", traced: true, prebury: "PreDrop - Legacy: C, PreBury - Legacy: A")));
    }

    [Fact]
    public void Pulled_jobs_are_split_by_how_far_the_tech_got()
    {
        const string sentinel = "9999-12-31 00:00";
        Block[] d0 =
        [
            Work("1", "C", traced: true, inprogress: "2026-10-06 09:00"),
            Work("2", "U", traced: true, inprogress: "2026-10-06 09:30"),
            Work("3", start: sentinel, kind: "job_moved", traced: true, enroute: "2026-10-06 08:00"),
            Work("4", start: sentinel, kind: "job_moved", traced: true, prebury: "PreDrop - Legacy: A"),
            Work("5", start: "2026-10-09 09:00"),
        ];
        var outcome = Outcomes.DayOutcome(
            Day,
            Snap(1, "2026-10-06 06:00"),
            [Work("1"), Work("2"), Work("3"), Work("4"), Work("5")],
            Checkpoints(("d0", d0))
        );
        Assert.Equal(
            [
                KeyValuePair.Create("total", 4),
                KeyValuePair.Create("in_progress", 1),
                KeyValuePair.Create("en_route", 1),
                KeyValuePair.Create("not_started", 1),
                KeyValuePair.Create("unknown", 1),
                KeyValuePair.Create("prereqs_open", 1),
            ],
            (OrderedDictionary<string, int>)Job(Outcomes.Summarise(outcome))["pulled_d0"]
        );
    }

    [Fact]
    public void Planned_job_that_loses_its_tech_is_unassigned()
    {
        var outcome = Outcomes.DayOutcome(
            Day,
            Snap(1, "2026-10-06 06:00"),
            [Work("1")],
            Checkpoints(("d0", [Work("1", kind: "job_unassigned", tech: "")]))
        );
        Assert.Equal([KeyValuePair.Create("d0", "unassigned")], outcome.Items[0].Outcomes);
    }

    [Fact]
    public void Planned_work_takes_the_techs_shift_region_and_its_task_type()
    {
        var job = Work("1", tech: "a") with { TaskType = "3" };
        var ownRegion = Work("T1", kind: "ticket", tech: "b") with { Region = "South" }; // keeps its own
        var outcome = Outcomes.DayOutcome(
            Day,
            Snap(1, "2026-10-06 06:00"),
            [job, ownRegion],
            Checkpoints(),
            new Dictionary<string, string> { ["a"] = "North", ["b"] = "North" }
        );
        Assert.Equal(
            new Dictionary<string, (string, string)> { ["1"] = ("North", "3"), ["T1"] = ("South", "") },
            outcome.Items.ToDictionary(i => i.RefId, i => (i.Region, i.TaskType))
        );
    }
}
