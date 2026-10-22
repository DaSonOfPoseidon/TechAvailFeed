using Microsoft.AspNetCore.Mvc;
using TechAvail.Core;
using TechAvail.Data;

namespace TechAvail.Api.Controllers;

public sealed class CapacityExportController(IFeedReads store, ApiSettings settings, TimeProvider clock) : V1Controller(store, settings, clock)
{
    // The next business days (Availability.ExportDates) as a workbook, with /calendar's other filters.
    [HttpGet("capacity.xlsx")]
    [Produces(Xlsx.ContentType)]
    public IActionResult Get(DateOnly? start, string? region, string? skill, string calendar = "install")
    {
        CheckCalendar(calendar);
        var (blocks, snapshot) = Latest();
        var from = start ?? Today();
        // Five business days span at most six dates: one Sunday.
        var (techDays, demand, _) = new CalendarController(Store, Settings, Clock).CapacityFor(blocks, from, from.AddDays(5), region, skill, calendar);
        var dates = Availability.ExportDates(from, techDays);
        var to = dates[^1];
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
            WindowSummary.Build(techDays, demand, dates, LocalNow(), calendar),
            [.. demand.Where(b => dates.Contains(b.WorkDate))],
            calendar == "install" ? InstallAvailability.Build(techDays, dates, Today(), region) : null
        );
        return Workbook(data, $"capacity_{Today():yyyy-MM-dd}.xlsx");
    }
}
