using Microsoft.AspNetCore.Mvc;
using TechAvail.Core;
using TechAvail.Core.Parsing;
using TechAvail.Data;

namespace TechAvail.Api.Controllers;

public sealed class CalendarController(IFeedReads store, ApiSettings settings, TimeProvider clock) : V1Controller(store, settings, clock)
{
    public sealed record Filters(string? Region, string? Skill, string Calendar);

    public sealed record Interval(DateTime Start, DateTime End);

    public sealed record WorkRow(
        string Kind,
        string RefId,
        string Status,
        string TaskType,
        string Region,
        DateTime StartsAt,
        DateTime EndsAt,
        string? AddressIssue
    )
    {
        public static WorkRow From(Block b) => new(b.Kind, b.RefId, b.Status, b.TaskType, b.Region, b.StartsAt, b.EndsAt, b.AddressIssue);
    }

    public sealed record Free(DateTime OpenFrom, DateTime OpenUntil, int OpenMinutes);

    public sealed record TechDetail(
        string TechId,
        string TechName,
        string Region,
        string Skills,
        double ShiftH,
        double LunchH,
        double TimeOffH,
        double AvailableH,
        double BookedH,
        double FreeH,
        int Jobs,
        int Tickets,
        bool OnTimeOff,
        List<Interval> Shifts,
        List<Interval> TimeOff,
        List<WorkRow> Work,
        List<Free> Free
    )
    {
        public static TechDetail From(TechDay d) =>
            new(
                d.TechId,
                d.TechName,
                d.Region,
                d.Skills,
                d.ShiftHours,
                d.LunchHours,
                d.TimeOffHours,
                d.AvailableHours,
                d.BookedHours,
                d.FreeHours,
                d.Jobs,
                d.Tickets,
                d.OnTimeOff,
                [.. d.Shifts.Select(s => new Interval(s.Start, s.End))],
                [.. d.TimeOff.Select(s => new Interval(s.Start, s.End))],
                [.. d.Work.Select(WorkRow.From)],
                [.. d.Free.Select(s => new Free(s.OpenFrom, s.OpenUntil, s.OpenMinutes))]
            );
    }

    public sealed record RangeResponse(SnapshotInfo? Snapshot, Filters Filters, DateOnly Start, DateOnly End, List<CalendarEntry> Days);

    public sealed record DayResponse(
        SnapshotInfo? Snapshot,
        Filters Filters,
        DateOnly Date,
        Capacity Totals,
        List<RegionCapacity> ByRegion,
        List<TechDetail> Techs,
        List<WorkRow> Unassigned
    );

    // The filtered tech days, the unassigned demand and the calendar entries for [start, end].
    internal (List<TechDay> Days, List<Block> Demand, List<CalendarEntry> Entries) CapacityFor(
        List<Block> blocks,
        DateOnly start,
        DateOnly end,
        string? region,
        string? skill,
        string calendar
    )
    {
        var found = Availability.TechDays(blocks, LocalNow(), start, end, calendar);
        var days = CapacityRollup.FilterDays(found, region, skill);
        var demand = CapacityRollup.UnassignedWork(blocks, start, end, region, calendar);
        return (days, demand, CapacityRollup.Entries(days, demand, start, end));
    }

    // Capacity per day and region: available, booked, free and unassigned hours, utilisation.
    [HttpGet("calendar")]
    public RangeResponse Range(DateOnly? start, string? region, string? skill, int days = 30, string calendar = "install")
    {
        CheckDays(days);
        CheckCalendar(calendar);
        var (blocks, snapshot) = Latest();
        var (from, to) = Window(start, days);
        var (_, _, entries) = CapacityFor(blocks, from, to, region, skill, calendar);
        return new RangeResponse(Info(snapshot), new Filters(region, skill, calendar), from, to, entries);
    }

    // One day per technician: shifts, time off, booked work, free slots.
    [HttpGet("calendar/{day}")]
    public DayResponse Day(DateOnly day, string? region, string? skill, string calendar = "install")
    {
        CheckCalendar(calendar);
        var (blocks, snapshot) = Latest();
        var (days, demand, entries) = CapacityFor(blocks, day, day, region, skill, calendar);
        var entry = entries.Single();
        return new DayResponse(
            Info(snapshot),
            new Filters(region, skill, calendar),
            entry.Date,
            entry.Totals,
            entry.ByRegion,
            [.. days.Select(TechDetail.From)],
            [.. demand.Select(WorkRow.From)]
        );
    }
}
