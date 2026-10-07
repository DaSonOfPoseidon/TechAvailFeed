using Microsoft.AspNetCore.Mvc;
using TechAvail.Data;

namespace TechAvail.Api.Controllers;

public sealed class KpisController(FeedStore store, ApiSettings settings, TimeProvider clock) : V1Controller(store, settings, clock)
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

    // Completion, cancellation and reschedule rates per day, region and technician.
    [HttpGet("kpis/outcomes")]
    public OrderedDictionary<string, object?> Outcomes(DateOnly? start, DateOnly? end, string? region, string? tech)
    {
        var (from, to) = HistoryWindow(start, end);
        var history = new OutcomeHistory(Store, Settings.Tz, Clock);
        var response = new OrderedDictionary<string, object?>
        {
            ["snapshot"] = Info(Store.SnapshotMeta()),
            ["filters"] = new OrderedDictionary<string, object?> { ["region"] = region, ["tech"] = tech },
            ["start"] = from,
            ["end"] = to,
        };
        foreach (var (key, value) in Kpis.OutcomeKpis(history.Range(from, to), region, tech))
            response[key] = value;
        return response;
    }
}
