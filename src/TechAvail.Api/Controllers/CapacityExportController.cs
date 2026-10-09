using Microsoft.AspNetCore.Mvc;
using TechAvail.Core;
using TechAvail.Core.Parsing;
using TechAvail.Data;

namespace TechAvail.Api.Controllers;

public sealed class CapacityExportController(IFeedReads store, ApiSettings settings, TimeProvider clock) : V1Controller(store, settings, clock)
{
    static readonly string[] WorkKinds = ["job", "ticket", "time_off"];

    // The raw rows behind the filtered tech days: their shifts, work and any overlapping leave.
    internal static List<Block> ScheduleRows(List<Block> blocks, List<TechDay> days, DateOnly start, DateOnly end, string calendar)
    {
        var keys = days.Select(d => (d.TechId, d.WorkDate)).ToHashSet();
        var techs = days.Select(d => d.TechId).ToHashSet();
        var shiftKind = Availability.Calendars[calendar];
        var rows = blocks.Where(b =>
            b.Kind == "time_off"
                ? techs.Contains(b.TechId) && DateOnly.FromDateTime(b.StartsAt) <= end && DateOnly.FromDateTime(b.EndsAt) >= start
                : (b.Kind == shiftKind || WorkKinds.Contains(b.Kind)) && keys.Contains((b.TechId, b.WorkDate))
        );
        return
        [
            .. rows.OrderBy(b => b.WorkDate)
                .ThenBy(b => b.TechName, StringComparer.Ordinal)
                .ThenBy(b => b.StartsAt)
                .ThenBy(b => b.Kind, StringComparer.Ordinal),
        ];
    }

    // The forward calendar (same filters as /calendar) as a workbook.
    [HttpGet("capacity.xlsx")]
    [Produces(Xlsx.ContentType)]
    public IActionResult Get(DateOnly? start, string? region, string? skill, int days = 30, string calendar = "install")
    {
        CheckDays(days);
        CheckCalendar(calendar);
        var (blocks, snapshot) = Latest();
        var (from, to) = Window(start, days);
        var (techDays, demand, entries) = new CalendarController(Store, Settings, Clock).CapacityFor(blocks, from, to, region, skill, calendar);
        var data = CapacityExport.Workbook(
            Settings.Tz,
            Clock.GetUtcNow(),
            snapshot.Id,
            snapshot.GeneratedAt,
            new()
            {
                ["calendar"] = calendar,
                ["region"] = region,
                ["skill"] = skill,
                ["from"] = from,
                ["to"] = to,
            },
            entries,
            techDays,
            demand,
            ScheduleRows(blocks, techDays, from, to, calendar)
        );
        return Workbook(data, $"capacity_{Today():yyyy-MM-dd}.xlsx");
    }
}
