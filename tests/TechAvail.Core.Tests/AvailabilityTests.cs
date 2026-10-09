using TechAvail.Core.Parsing;

namespace TechAvail.Core.Tests;

public class AvailabilityTests
{
    static readonly DateOnly Tuesday = new(2026, 10, 6);
    static readonly DateOnly Wednesday = new(2026, 10, 7);
    static readonly DateOnly OldSaturday = new(2026, 10, 10);
    static readonly DateOnly Saturday = new(2026, 10, 17);
    static readonly DateTime Early = new(2026, 10, 1, 7, 0, 0);

    static DateTime At(DateOnly day, string hhmm) => day.ToDateTime(TimeOnly.Parse(hhmm));

    static Block B(string kind, DateOnly day, string start, string end, string tech = "t1", string status = "A") =>
        new()
        {
            Kind = kind,
            WorkDate = day,
            TechId = tech,
            TechName = tech.ToUpperInvariant(),
            StartsAt = At(day, start),
            EndsAt = At(day, end),
            RefId = "",
            Status = status,
            Region = kind == "shift" ? "North" : "",
            Skills = kind == "shift" ? "INS" : "",
        };

    static List<(string, string)> Windows(IEnumerable<FreeSlot> slots) =>
        [.. slots.Select(s => (s.OpenFrom.ToString("HH:mm"), s.OpenUntil.ToString("HH:mm")))];

    [Fact]
    public void Open_day_is_split_only_by_lunch()
    {
        var slots = Availability.FreeSlots([B("shift", Tuesday, "08:00", "17:00")], Early);
        Assert.Equal([("08:00", "12:00"), ("13:00", "17:00")], Windows(slots));
        Assert.Equal("North", slots[0].Region);
        Assert.Equal(240, slots[0].OpenMinutes);
    }

    [Fact]
    public void Saturday_lunch_matches_weekdays() =>
        Assert.Equal(
            [("08:00", "12:00"), ("13:00", "17:00")],
            Windows(Availability.FreeSlots([B("shift", Saturday, "08:00", "17:00")], Early))
        );

    [Fact]
    public void Saturdays_before_the_change_keep_the_later_lunch() =>
        Assert.Equal(
            [("08:00", "13:00"), ("14:00", "17:00")],
            Windows(Availability.FreeSlots([B("shift", OldSaturday, "08:00", "17:00")], Early))
        );

    [Fact]
    public void Overlapping_busy_blocks_merge() =>
        Assert.Equal(
            [("10:30", "12:00")],
            Windows(
                Availability.FreeSlots(
                    [
                        B("shift", Tuesday, "08:00", "12:00"),
                        B("job", Tuesday, "08:30", "10:00"),
                        B("ticket", Tuesday, "09:30", "10:30"),
                    ],
                    Early
                )
            )
        );

    [Fact]
    public void Split_shift_gap_is_not_free() =>
        Assert.Equal(
            [("08:00", "11:00"), ("14:00", "17:00")],
            Windows(
                Availability.FreeSlots(
                    [B("shift", Tuesday, "08:00", "11:00"), B("shift", Tuesday, "14:00", "17:00")],
                    Early
                )
            )
        );

    [Fact]
    public void Short_gaps_are_dropped()
    {
        Block[] blocks = [B("shift", Tuesday, "08:00", "11:00"), B("job", Tuesday, "08:45", "11:00")];
        Assert.Empty(Availability.FreeSlots(blocks, Early));
        Assert.Equal([("08:00", "08:45")], Windows(Availability.FreeSlots(blocks, Early, minMinutes: 30)));
    }

    [Fact]
    public void Todays_leading_gap_is_clipped_not_dropped() =>
        Assert.Equal(
            [("13:40", "17:00")],
            Windows(Availability.FreeSlots([B("shift", Tuesday, "08:00", "17:00")], At(Tuesday, "13:10")))
        );

    [Fact]
    public void Multi_day_time_off_blocks_every_day_it_covers()
    {
        var timeOff = B("time_off", Tuesday, "00:00", "00:00") with { EndsAt = At(new DateOnly(2026, 10, 8), "00:00") };
        Block[] blocks = [B("shift", Tuesday, "08:00", "17:00"), B("shift", Wednesday, "08:00", "17:00"), timeOff];
        Assert.Empty(Availability.FreeSlots(blocks, Early));
    }

    [Fact]
    public void Busy_time_only_affects_its_own_tech() =>
        Assert.Equal(
            ["b"],
            Availability
                .FreeSlots(
                    [
                        B("shift", Tuesday, "08:00", "12:00", tech: "a"),
                        B("shift", Tuesday, "08:00", "12:00", tech: "b"),
                        B("job", Tuesday, "08:00", "12:00", tech: "a"),
                    ],
                    Early
                )
                .Select(s => s.TechId)
        );

    [Fact]
    public void Dead_jobs_closed_tickets_and_moved_rows_are_not_busy() =>
        Assert.Equal(
            [("08:00", "12:00")],
            Windows(
                Availability.FreeSlots(
                    [
                        B("shift", Tuesday, "08:00", "12:00"),
                        B("job", Tuesday, "08:00", "09:00", status: "X"),
                        B("ticket", Tuesday, "09:00", "10:00", status: "C"),
                        B("job_moved", Tuesday, "10:00", "11:00"),
                    ],
                    Early
                )
            )
        );

    [Fact]
    public void Held_task_blocks_but_held_ticket_does_not() =>
        Assert.Equal(
            [("10:00", "12:00")],
            Windows(
                Availability.FreeSlots(
                    [
                        B("shift", Tuesday, "08:00", "12:00"),
                        B("job", Tuesday, "08:00", "10:00", status: "H"),
                        B("ticket", Tuesday, "10:00", "12:00", status: "H"),
                    ],
                    Early
                )
            )
        );

    [Fact]
    public void Clip_rounds_up_to_the_minute()
    {
        var now = new DateTime(2026, 10, 6, 13, 10, 51).AddTicks(50_000);
        var slots = Availability.FreeSlots([B("shift", Tuesday, "08:00", "17:00")], now);
        Assert.Equal(At(Tuesday, "13:41"), slots[0].OpenFrom);
    }

    static Block Unassigned(string kind, DateOnly day, string start, string end, string region, string status = "A") =>
        B(kind, day, start, end, tech: "", status: status) with { Region = region };

    [Fact]
    public void Unassigned_work_takes_no_tech_time() =>
        Assert.Equal(
            [("08:00", "12:00"), ("13:00", "17:00")],
            Windows(
                Availability.FreeSlots(
                    [
                        B("shift", Tuesday, "08:00", "17:00"),
                        Unassigned("job_unassigned", Tuesday, "08:00", "17:00", "North"),
                    ],
                    Early
                )
            )
        );

    [Fact]
    public void Unassigned_demand_is_summed_per_day_and_region()
    {
        Block[] blocks =
        [
            Unassigned("job_unassigned", Tuesday, "08:00", "10:00", "North"),
            Unassigned("job_unassigned", Tuesday, "13:00", "14:30", "North"),
            Unassigned("ticket_unassigned", Tuesday, "10:00", "11:00", "North"),
            Unassigned("job_unassigned", Tuesday, "08:00", "10:00", "South"),
            Unassigned("job_unassigned", Tuesday, "08:00", "10:00", "North", status: "X"), // dead
            Unassigned("ticket_unassigned", Tuesday, "08:00", "10:00", "North", status: "H"), // held
            Unassigned("job_unassigned", new DateOnly(2026, 10, 5), "08:00", "10:00", "North"), // past
            B("job", Tuesday, "08:00", "10:00"), // assigned: not demand
        ];
        Assert.Equal(
            [("North", 2, 1, 4.5), ("South", 1, 0, 2.0)],
            Availability.Unassigned(blocks, Tuesday).Select(d => (d.Region, d.Jobs, d.Tickets, d.Hours))
        );
    }

    [Fact]
    public void Tech_day_splits_the_shift_into_lunch_time_off_booked_and_free()
    {
        var day = Assert.Single(
            Availability.TechDays(
                [
                    B("shift", Tuesday, "08:00", "17:00"),
                    B("job", Tuesday, "08:00", "10:00"),
                    B("ticket", Tuesday, "11:30", "12:30"), // overlaps lunch: counted once
                    B("time_off", Tuesday, "15:00", "23:59"),
                ],
                Early
            )
        );
        Assert.Equal((9.0, 1.0, 2.0), (day.ShiftHours, day.LunchHours, day.TimeOffHours));
        Assert.Equal(6, day.AvailableHours);
        Assert.Equal(2.5, day.BookedHours);
        // 10:00-11:30 and 13:00-15:00; the free time between bookings, at least an hour each.
        Assert.Equal(3.5, day.FreeHours);
        Assert.Equal((1, 1), (day.Jobs, day.Tickets));
        Assert.True(day.OnTimeOff);
        Assert.Equal("North", day.Region);
        Assert.Equal(["10:00", "13:00"], day.Free.Select(s => s.OpenFrom.ToString("HH:mm")));
    }

    [Fact]
    public void Tech_day_ignores_dead_work_and_counts_jobs_once()
    {
        var day = Assert.Single(
            Availability.TechDays(
                [
                    B("shift", Tuesday, "08:00", "12:00"),
                    B("job", Tuesday, "08:00", "09:00", status: "X"),
                    B("ticket", Tuesday, "09:00", "10:00", status: "C"),
                    B("job_moved", Tuesday, "10:00", "11:00"),
                ],
                Early
            )
        );
        Assert.Equal((0.0, 0, 0), (day.BookedHours, day.Jobs, day.Tickets));
    }

    [Fact]
    public void Leave_without_a_shift_is_a_day_off_with_no_capacity()
    {
        var leave = B("time_off", Tuesday, "00:00", "23:59") with { EndsAt = At(Wednesday, "23:59") };
        var days = Availability.TechDays([leave], Early, start: Tuesday, end: Wednesday);
        Assert.Equal(
            [(Tuesday, true, 0.0), (Wednesday, true, 0.0)],
            days.Select(d => (d.WorkDate, d.OnTimeOff, d.ShiftHours))
        );
    }

    [Fact]
    public void Leave_is_only_expanded_inside_the_requested_range()
    {
        var leave = B("time_off", Tuesday, "00:00", "23:59") with
        {
            StartsAt = At(new DateOnly(2026, 9, 1), "00:00"),
            EndsAt = At(new DateOnly(2027, 10, 1), "23:59"),
        };
        Assert.Equal([Tuesday], Availability.TechDays([leave], Early, start: Tuesday, end: Tuesday).Select(d => d.WorkDate));
    }

    [Fact]
    public void Lead_time_only_clips_free_hours()
    {
        var day = Assert.Single(Availability.TechDays([B("shift", Tuesday, "08:00", "17:00")], At(Tuesday, "13:10")));
        Assert.Equal(8, day.AvailableHours);
        Assert.Equal(Math.Round(200.0 / 60, 2), day.FreeHours);
    }

    [Fact]
    public void Tc_shifts_are_not_install_capacity()
    {
        Block[] blocks =
        [
            B("shift", Tuesday, "08:00", "12:00", tech: "install"),
            B("shift_tc", Tuesday, "08:00", "12:00", tech: "trouble"),
            B("ticket", Tuesday, "08:00", "09:00", tech: "trouble"),
        ];
        Assert.Equal(["install"], Availability.FreeSlots(blocks, Early).Select(s => s.TechId));
        Assert.Equal(["install"], Availability.TechDays(blocks, Early).Select(d => d.TechId));
        var tc = Assert.Single(Availability.TechDays(blocks, Early, calendar: "tc"));
        Assert.Equal(("trouble", 1.0, 3.0), (tc.TechId, tc.BookedHours, tc.FreeHours));
        Assert.Equal([("09:00", "12:00")], Windows(Availability.FreeSlots(blocks, Early, calendar: "tc")));
    }

    [Fact]
    public void A_day_off_counts_on_the_techs_own_calendar()
    {
        Block[] blocks =
        [
            B("shift", Tuesday, "08:00", "17:00", tech: "install"),
            B("shift_tc", Tuesday, "08:00", "17:00", tech: "trouble"),
            B("time_off", Wednesday, "00:00", "23:59", tech: "install"),
            B("time_off", Wednesday, "00:00", "23:59", tech: "trouble"),
            B("time_off", Wednesday, "00:00", "23:59", tech: "leave"), // no shifts at all
        ];
        var install = Availability.TechDays(blocks, Early, start: Wednesday, end: Wednesday);
        var tc = Availability.TechDays(blocks, Early, start: Wednesday, end: Wednesday, calendar: "tc");
        // A tech with no shift of either kind stays on the install calendar, as before shift_tc.
        Assert.Equal(["install", "leave"], install.Select(d => d.TechId));
        Assert.Equal(["trouble"], tc.Select(d => d.TechId));
    }

    [Fact]
    public void Dual_tech_tickets_block_installs_but_jobs_dont_block_trouble_calls()
    {
        Block[] blocks =
        [
            B("shift", Tuesday, "08:00", "12:00", tech: "dual"),
            B("shift_tc", Tuesday, "08:00", "12:00", tech: "dual"),
            B("job", Tuesday, "08:00", "10:00", tech: "dual"),
            B("ticket", Tuesday, "10:00", "11:00", tech: "dual"),
        ];
        var install = Assert.Single(Availability.TechDays(blocks, Early));
        var tc = Assert.Single(Availability.TechDays(blocks, Early, calendar: "tc"));
        Assert.Equal((3.0, 1.0, 1, 1), (install.BookedHours, install.FreeHours, install.Jobs, install.Tickets));
        Assert.Equal((1.0, 3.0, 0, 1), (tc.BookedHours, tc.FreeHours, tc.Jobs, tc.Tickets));
        Assert.Equal(
            [("08:00", "10:00"), ("11:00", "12:00")],
            Windows(Availability.FreeSlots(blocks, Early, calendar: "tc"))
        );
    }

    [Theory]
    [InlineData("install")]
    [InlineData("tc")]
    public void A_dual_techs_day_off_counts_on_both_calendars(string calendar)
    {
        Block[] blocks =
        [
            B("shift", Tuesday, "08:00", "17:00", tech: "dual"),
            B("shift_tc", Tuesday, "08:00", "17:00", tech: "dual"),
            B("time_off", Wednesday, "00:00", "23:59", tech: "dual"),
        ];
        var days = Availability.TechDays(blocks, Early, start: Wednesday, end: Wednesday, calendar: calendar);
        Assert.Equal([("dual", true)], days.Select(d => (d.TechId, d.OnTimeOff)));
    }
}
