using Microsoft.AspNetCore.Mvc;
using TechAvail.Data;

namespace TechAvail.Api.Controllers;

public sealed class KpisController(IFeedReads store, ApiSettings settings, TimeProvider clock) : V1Controller(store, settings, clock)
{
    public sealed record CapacityResponse(
        SnapshotInfo? Snapshot,
        CalendarController.Filters Filters,
        List<DatedCapacity> Series,
        List<RegionTotal> ByRegion
    );

    // Daily capacity series plus totals per region.
    [HttpGet("kpis/capacity")]
    public CapacityResponse Capacity(DateOnly? start, string? region, string? skill, int days = 30, string calendar = "install")
    {
        CheckDays(days);
        CheckCalendar(calendar);
        var (blocks, snapshot) = Latest();
        var (from, to) = Window(start, days);
        var calendarController = new CalendarController(Store, Settings, Clock);
        var (_, _, entries) = calendarController.CapacityFor(blocks, from, to, region, skill, calendar);
        return new CapacityResponse(
            Info(snapshot),
            new CalendarController.Filters(region, skill, calendar),
            [.. entries.Select(e => e.Totals.ForDate(e.Date))],
            CapacityRollup.RegionTotals(entries)
        );
    }

    public sealed record OutcomeFilters(string? Region, string? Tech);

    public sealed record OutcomesResponse(
        SnapshotInfo? Snapshot,
        OutcomeFilters Filters,
        DateOnly Start,
        DateOnly End,
        List<DayKpis> Days,
        ByKind Totals,
        List<RegionKpis> ByRegion,
        List<TechKpis> ByTech
    );

    // Completion, cancellation and reschedule rates per day, region and technician.
    [HttpGet("kpis/outcomes")]
    public OutcomesResponse Outcomes(DateOnly? start, DateOnly? end, string? region, string? tech)
    {
        var (from, to) = HistoryWindow(start, end);
        var kpis = Kpis.OutcomeKpis(new OutcomeHistory(Store, Settings.Tz, Clock).Range(from, to), region, tech);
        return new OutcomesResponse(
            Info(Store.SnapshotMeta()),
            new OutcomeFilters(region, tech),
            from,
            to,
            kpis.Days,
            kpis.Totals,
            kpis.ByRegion,
            kpis.ByTech
        );
    }
}
