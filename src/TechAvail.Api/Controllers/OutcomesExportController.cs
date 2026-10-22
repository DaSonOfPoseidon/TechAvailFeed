using Microsoft.AspNetCore.Mvc;
using TechAvail.Data;

namespace TechAvail.Api.Controllers;

public sealed class OutcomesExportController(IFeedReads store, ApiSettings settings, TimeProvider clock) : V1Controller(store, settings, clock)
{
    [HttpGet("outcomes.xlsx")]
    [Produces(Xlsx.ContentType)]
    public IActionResult Get(DateOnly? start, DateOnly? end, string? region, string? tech)
    {
        var (from, to) = HistoryWindow(start, end);
        var outcomes = new OutcomeHistory(Store, Settings.Tz, Clock).Range(from, to);
        var data = OutcomesExport.Workbook(Settings.Tz, Clock.GetUtcNow(), from, to, region, tech, outcomes);
        return Workbook(data, $"outcomes_{Today():yyyy-MM-dd}.xlsx");
    }
}
