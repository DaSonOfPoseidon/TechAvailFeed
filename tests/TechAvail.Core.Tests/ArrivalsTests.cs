using System.Globalization;
using TechAvail.Core.Parsing;

namespace TechAvail.Core.Tests;

public class ArrivalsTests
{
    static readonly DateOnly Day = new(2026, 10, 6);
    static readonly DateTime At815 = T("2026-10-06 08:15");

    static DateTime T(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture);

    static Block Work(
        string reference,
        string status = "A",
        string start = "2026-10-06 08:00",
        string kind = "job",
        string tech = "t1",
        string department = "FIELD",
        string? enroute = null,
        string? inprogress = null
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
            EndsAt = startsAt.AddHours(2),
            RefId = reference,
            Status = status,
            Department = department,
            Region = "North",
            Skills = "",
            TaskType = kind == "job" ? "3" : "",
            EnrouteAt = enroute is null ? null : T(enroute),
            InprogressAt = inprogress is null ? null : T(inprogress),
        };
    }

    [Fact]
    public void The_815_run_is_the_first_snapshot_from_815_until_nine()
    {
        Snapshot[] snaps = [new(1, T("2026-10-06 08:00")), new(2, T("2026-10-06 08:26")), new(3, T("2026-10-06 08:45"))];
        Assert.Equal(2, Arrivals.FindEightFifteen(snaps, Day)!.Id);
        Assert.Null(Arrivals.FindEightFifteen([new(1, T("2026-10-06 08:14")), new(2, T("2026-10-06 09:00"))], Day));
    }

    [Fact]
    public void Last_snapshot_on_the_day_ignores_the_next_day()
    {
        Snapshot[] snaps = [new(1, T("2026-10-06 22:45")), new(2, T("2026-10-06 23:45")), new(3, T("2026-10-07 00:00"))];
        Assert.Equal(2, Arrivals.FindLastOn(snaps, Day)!.Id);
    }

    [Fact]
    public void Eight_am_state_follows_the_status_ladder()
    {
        var rows = Arrivals.EightAm(
            [
                Work("c", "C", tech: "d", enroute: "2026-10-06 07:30", inprogress: "2026-10-06 07:58"),
                Work("i", "I", tech: "c", enroute: "2026-10-06 07:40", inprogress: "2026-10-06 08:05"),
                Work("e", "E", tech: "b", enroute: "2026-10-06 07:50"),
                Work("a", "A", tech: "a"),
            ],
            Day,
            At815
        );
        Assert.Equal(
            [("a", "not_started", null, null), ("e", "en_route", null, 25), ("i", "arrived", 5, 25), ("c", "arrived", -2, 28)],
            rows.Select(r => (r.RefId, r.State, r.MinutesLate, r.MinutesEnRoute))
        );
    }

    [Fact]
    public void Arrival_on_a_later_job_counts_and_is_negative()
    {
        var row = Assert.Single(
            Arrivals.EightAm(
                [Work("first", "A"), Work("later", "I", start: "2026-10-06 10:00", enroute: "2026-10-06 07:45", inprogress: "2026-10-06 07:55")],
                Day,
                At815
            )
        );
        Assert.Equal(("first", "arrived", -5, 10), (row.RefId, row.State, row.MinutesLate, row.MinutesEnRoute));
    }

    [Fact]
    public void En_route_to_another_job_is_yellow()
    {
        var row = Assert.Single(
            Arrivals.EightAm([Work("first", "A"), Work("later", "E", start: "2026-10-06 10:00", enroute: "2026-10-06 08:08")], Day, At815)
        );
        Assert.Equal(("en_route", 7), (row.State, row.MinutesEnRoute));
    }

    [Fact]
    public void Marks_from_another_day_or_after_the_snapshot_are_ignored()
    {
        var row = Assert.Single(
            Arrivals.EightAm([Work("j", "A", enroute: "2026-10-05 07:50", inprogress: "2026-10-06 08:20")], Day, At815)
        );
        Assert.Equal(("not_started", null, null), (row.State, row.EnrouteAt, row.InprogressAt));
    }

    [Fact]
    public void Out_of_scope_work_is_left_out()
    {
        var rows = Arrivals.EightAm(
            [
                Work("h", "H", tech: "a"),
                Work("x", "X", tech: "b"),
                Work("u", "U", tech: "c"),
                Work("con", "A", tech: "d", department: "FLDSVCCON"),
                Work("nine", "A", tech: "e", start: "2026-10-06 09:00"),
                Work("moved", "A", tech: "f", kind: "job_moved"),
                Work("open", "O", tech: "g", kind: "ticket", department: "TC"),
            ],
            Day,
            At815
        );
        var row = Assert.Single(rows);
        Assert.Equal(("open", "ticket", "not_started", null), (row.RefId, row.Kind, row.State, row.EnrouteAt));
    }

    [Fact]
    public void In_progress_tier_puts_the_newest_arrival_first()
    {
        var rows = Arrivals.EightAm(
            [
                Work("early", "I", tech: "a", inprogress: "2026-10-06 07:50"),
                Work("blank", "I", tech: "b"),
                Work("late", "I", tech: "c", inprogress: "2026-10-06 08:10"),
            ],
            Day,
            At815
        );
        Assert.Equal(["late", "early", "blank"], rows.Select(r => r.RefId));
    }

    [Fact]
    public void So_far_has_started_windows_and_everything_completed()
    {
        var at = T("2026-10-06 16:00");
        var rows = Arrivals.SoFar(
            [
                Work("due", "A", tech: "a", start: "2026-10-06 15:00"),
                Work("future", "A", tech: "b", start: "2026-10-06 17:00"),
                Work("early", "C", tech: "c", start: "2026-10-06 17:00", inprogress: "2026-10-06 14:00"),
                Work("driving", "E", tech: "d", start: "2026-10-06 10:00", enroute: "2026-10-06 15:30"),
            ],
            Day,
            at
        );
        Assert.Equal(
            [("due", "not_started"), ("driving", "en_route"), ("early", "arrived")],
            rows.Select(r => (r.RefId, r.State))
        );
        Assert.Equal(30, rows[1].MinutesEnRoute);
    }

    [Fact]
    public void Completed_lists_each_techs_finished_work_in_order()
    {
        var at = T("2026-10-06 23:45");
        var rows = Arrivals.Completed(
            [
                Work("b2", "C", tech: "b", start: "2026-10-06 13:00", enroute: "2026-10-06 12:40", inprogress: "2026-10-06 13:05"),
                Work("b1", "C", tech: "b", enroute: "2026-10-06 07:45", inprogress: "2026-10-06 08:02"),
                Work("a1", "R", tech: "a", kind: "ticket", department: "TC"),
                Work("a2", "I", tech: "a", start: "2026-10-06 10:00"),
                Work("a3", "N", tech: "a", start: "2026-10-06 12:00"),
            ],
            Day,
            at
        );
        Assert.Equal(
            [("a1", null, null), ("b1", 2, 17), ("b2", 5, 25)],
            rows.Select(r => (r.RefId, r.MinutesLate, r.MinutesEnRoute))
        );
    }
}
