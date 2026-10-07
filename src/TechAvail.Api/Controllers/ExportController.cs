using Microsoft.AspNetCore.Mvc;
using TechAvail.Core;
using TechAvail.Core.Parsing;
using TechAvail.Data;

namespace TechAvail.Api.Controllers;

public sealed class ExportController(FeedStore store, ApiSettings settings, TimeProvider clock) : V1Controller(store, settings, clock)
{
    // All of the above as a multi-sheet workbook: the forward calendar from start, plus the last
    // history_days of outcomes up to today.
    [HttpGet("export.xlsx")]
    [Produces(Xlsx.ContentType)]
    public IActionResult Get(
        DateOnly? start,
        string? region,
        string? skill,
        int days = 30,
        [FromQuery(Name = "history_days")] int historyDays = 30,
        string calendar = "install"
    )
    {
        CheckDays(days);
        CheckDays(historyDays, MaxHistoryDays);
        CheckCalendar(calendar);
        var (blocks, snapshot) = Latest();
        var (from, to) = Window(start, days);
        var (techDays, demand, entries) = new CalendarController(Store, Settings, Clock).CapacityFor(blocks, from, to, region, skill, calendar);
        var today = Today();
        var asOf = TimeZoneInfo.ConvertTime(snapshot.GeneratedAt ?? Clock.GetUtcNow(), Settings.Tz).DateTime;
        var jeopardy = Jeopardy.Find(blocks, today, asOf).Where(r => region is null || r.Job.Region == region).ToList();
        var past = new OutcomeHistory(Store, Settings.Tz, Clock).Range(today.AddDays(-(historyDays - 1)), today);
        var data = Export.Workbook(
            Settings.Tz,
            Clock.GetUtcNow(),
            snapshot.Id,
            snapshot.GeneratedAt,
            new OrderedDictionary<string, object?>
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
            CapacityExportController.ScheduleRows(blocks, techDays, from, to, calendar),
            jeopardy,
            past,
            Kpis.OutcomeKpis(past, region),
            Diagnostics.Diagnose(blocks, today)
        );
        return Workbook(data, $"techavail_{today:yyyy-MM-dd}.xlsx");
    }
}
