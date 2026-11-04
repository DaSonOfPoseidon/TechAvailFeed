using System.Globalization;
using TechAvail.Core.Parsing;

namespace TechAvail.Core.Tests;

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
        string prebury = "",
        string? reason = null
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
            IncompleteReason = reason,
        };
    }

    static Dictionary<string, IReadOnlyList<Block>?> Checkpoints(params (string, Block[]?)[] items) =>
        items.ToDictionary(i => i.Item1, i => (IReadOnlyList<Block>?)i.Item2);

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
        var (job, ticket) = (summary.Job, summary.Ticket);
        Assert.Equal((2, 1, 2, 2, 1), (job.Planned, job.CompletedD0, job.CompletedD1, job.CompletedD2, job.AddedAfterMorning));
        Assert.Equal((1, 1), (ticket.Planned, ticket.CompletedD0));
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
        var pulled = Outcomes.Summarise(outcome).Job.PulledD0;
        Assert.Equal((4, 1, 1, 1, 1, 1), (pulled.Total, pulled.InProgress, pulled.EnRoute, pulled.NotStarted, pulled.Unknown, pulled.PrereqsOpen));
        Assert.Equal((0, 0, 4), (pulled.Customer, pulled.Socket, pulled.NoReason));
        Assert.Empty(pulled.ByReason);
    }

    [Theory]
    [InlineData("CX_REQUEST", "canceled", "customer")]
    [InlineData("SX_NOCONTACT", "canceled", "socket")]
    [InlineData("CR_REQUEST", "rescheduled", "customer")]
    [InlineData("SR_WEATHER", "rescheduled", "socket")]
    [InlineData("CU_REQUEST", "unscheduled", "customer")]
    [InlineData("SU_PERMIT", "unscheduled", "socket")]
    public void The_reason_prefix_gives_the_bucket_and_who_owns_it(string code, string bucket, string owner)
    {
        Assert.Equal(new IncompleteReason(code, bucket, owner), IncompleteReasons.Parse(code));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AdditionalW")]
    [InlineData("XX_REQUEST")]
    [InlineData("ZZ_CX_REQUEST")] // a deactivated code
    [InlineData("CX")]
    [InlineData("cx_request")]
    public void Anything_but_the_six_prefixes_is_not_recognized(string? code)
    {
        Assert.Null(IncompleteReasons.Parse(code));
    }

    [Theory]
    [InlineData("N", "2026-10-06 09:00", "SX_NOCONTACT", "canceled")] // today: open
    [InlineData("N", "9999-12-31 00:00", "CR_REQUEST", "rescheduled")] // today: unscheduled
    [InlineData("N", "2026-10-09 09:00", "CU_REQUEST", "unscheduled")] // today: rescheduled
    [InlineData("A", "9999-12-31 00:00", "SR_WEATHER", "rescheduled")] // no start: the code decides
    [InlineData("A", "2026-10-06 09:00", "CR_REQUEST", "open")] // stale code on a job still on the plan
    [InlineData("A", "2026-10-09 09:00", "CX_REQUEST", "rescheduled")]
    [InlineData("X", "9999-12-31 00:00", "CR_REQUEST", "canceled")]
    [InlineData("C", "9999-12-31 00:00", "CR_REQUEST", "completed")]
    [InlineData("N", "9999-12-31 00:00", "AdditionalW", "unscheduled")]
    [InlineData("N", "2026-10-06 09:00", null, "open")]
    public void A_recognized_code_sets_a_new_or_unscheduled_jobs_bucket(string status, string start, string? code, string expected)
    {
        Assert.Equal(expected, Outcomes.Classify("job", Day, Work("1", status, start, reason: code)));
    }

    [Fact]
    public void A_handed_off_job_stays_handed_off_unless_it_is_new()
    {
        Assert.Equal("handed_off", Outcomes.Classify("job", Day, Work("1", "A", department: "OTHER", reason: "CR_REQUEST")));
        Assert.Equal("rescheduled", Outcomes.Classify("job", Day, Work("1", "N", department: "OTHER", reason: "CR_REQUEST")));
    }

    [Fact]
    public void A_missing_row_stays_missing_and_a_ticket_ignores_the_code()
    {
        Assert.Equal("missing", Outcomes.Classify("job", Day, null));
        Assert.Equal("open", Outcomes.Classify("ticket", Day, Work("T1", "N", kind: "ticket", reason: "SX_NOCONTACT")));
    }

    [Fact]
    public void The_plan_day_keeps_the_reason_from_d0()
    {
        var d0 = Work("1", "N", reason: "SX_NOCONTACT");
        var d1 = Work("1", "N", reason: "CR_REQUEST");
        var outcome = Outcomes.DayOutcome(Day, Snap(1, "2026-10-06 06:00"), [Work("1")], Checkpoints(("d0", [d0]), ("d1", [d1])));
        Assert.Equal("SX_NOCONTACT", outcome.Items.Single().IncompleteReason);
    }

    [Fact]
    public void Pulled_jobs_are_split_by_who_owns_the_reason_and_counted_per_code()
    {
        const string sentinel = "9999-12-31 00:00";
        Block[] d0 =
        [
            Work("1", "N", reason: "SX_NOCONTACT"), // canceled, Socket
            Work("2", "N", reason: "SX_NOCONTACT"),
            Work("3", "N", start: sentinel, reason: "CR_REQUEST"), // rescheduled, customer
            Work("4", "X", start: sentinel, reason: "CR_REQUEST"), // canceled by status: the code is stale
            Work("5", "N", start: sentinel, reason: "AdditionalW"), // unscheduled, no reason
            Work("6", "N", start: sentinel), // unscheduled, no reason
            Work("7", "C", reason: "CX_REQUEST"), // not pulled
            Work("8", "A", reason: "CX_REQUEST"), // still open
        ];
        var outcome = Outcomes.DayOutcome(
            Day,
            Snap(1, "2026-10-06 06:00"),
            [.. Enumerable.Range(1, 8).Select(i => Work(i.ToString()))],
            Checkpoints(("d0", d0))
        );
        var pulled = Outcomes.Summarise(outcome).Job.PulledD0;
        Assert.Equal((6, 1, 2, 3), (pulled.Total, pulled.Customer, pulled.Socket, pulled.NoReason));
        Assert.Equal(new Dictionary<string, int> { ["SX_NOCONTACT"] = 2, ["CR_REQUEST"] = 1 }, pulled.ByReason);
    }

    [Fact]
    public void A_code_whose_bucket_differs_from_the_final_one_is_no_reason()
    {
        var item = new Planned("job", "1", "t1", "T1", T("2026-10-06 09:00"))
        {
            Outcomes = new() { ["d0"] = "canceled" },
            IncompleteReason = "CR_REQUEST",
        };
        var stored = new Planned("job", "2", "t1", "T1", T("2026-10-06 09:00")) { Outcomes = new() { ["d0"] = "canceled" } };
        var pulled = Outcomes.PulledCounts([item, stored]);
        Assert.Equal((2, 0, 0, 2), (pulled.Total, pulled.Customer, pulled.Socket, pulled.NoReason));
        Assert.Empty(pulled.ByReason);
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
