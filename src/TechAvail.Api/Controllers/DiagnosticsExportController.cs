using Microsoft.AspNetCore.Mvc;
using TechAvail.Core;
using TechAvail.Data;

namespace TechAvail.Api.Controllers;

public sealed class DiagnosticsExportController(IFeedReads store, ApiSettings settings, TimeProvider clock) : V1Controller(store, settings, clock)
{
    // The data-quality checks (same filters as /diagnostics) as a workbook.
    [HttpGet("diagnostics.xlsx")]
    [Produces(Xlsx.ContentType)]
    public IActionResult Get(string? group, string? check)
    {
        var (blocks, snapshot) = Latest();
        var selected = DiagnosticsController.Select(Diagnostics.Diagnose(blocks, Today()), group, check);
        var data = DiagnosticsExport.Workbook(Settings.Tz, Clock.GetUtcNow(), snapshot.Id, snapshot.GeneratedAt, group, check, selected);
        return Workbook(data, $"diagnostics_{Today():yyyy-MM-dd}.xlsx");
    }
}
