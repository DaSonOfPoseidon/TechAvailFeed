using System.Net;
using ClosedXML.Excel;
using TechAvail.Core;
using TechAvail.Data.Tests;

namespace TechAvail.Api.Tests;

public class DiagnosticsExportTests
{
    static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    static XLWorkbook Open(byte[] data) => new(new MemoryStream(data));

    static List<string> Row(IXLWorksheet ws, int row) => [.. ws.Row(row).Cells(1, ws.LastColumnUsed()!.ColumnNumber()).Select(c => c.GetString())];

    [Fact]
    public void Diagnostics_sheet_flattens_check_specific_fields_into_detail()
    {
        var check = new Check("tech_no_region", "tech_setup", "No region", "warning", "")
        {
            Rows = [new() { ["tech_id"] = "a", ["tech_name"] = "A", ["days"] = 3, ["first_date"] = new DateOnly(2026, 10, 6) }],
        };
        var missing = new Check("stale", "scheduling", "Stale", "info", "") { Available = false };
        using var wb = Open(DiagnosticsExport.Workbook(Chicago, Now, 1, null, null, null, [check, missing]));
        var ws = wb.Worksheet("Diagnostics");
        var header = Row(ws, 1);
        Assert.Equal("No region", ws.Cell(2, header.IndexOf("Check") + 1).GetText());
        Assert.Equal("a", ws.Cell(2, header.IndexOf("Tech id") + 1).GetText());
        Assert.Equal("days: 3; first date: 2026-10-06", ws.Cell(2, header.IndexOf("Detail") + 1).GetText());
        var about = wb.Worksheet("About").RowsUsed().ToDictionary(r => r.Cell(1).GetString(), r => r.Cell(2).Value);
        Assert.Equal(1, about["No region"].GetNumber());
        Assert.Equal("not in feed yet", about["Stale"].GetText());
    }

    [DbFact]
    public async Task Diagnostics_is_a_workbook_filtered_like_the_json()
    {
        using var db = new TestDatabase();
        CapacityExportTests.Seed(db);
        using var factory = new ApiFactory(db, Now);
        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/diagnostics.xlsx?group=scheduling");
        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("attachment; filename=\"diagnostics_2026-10-06.xlsx\"", response.Content.Headers.GetValues("Content-Disposition").Single());
        using var wb = Open(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(["Diagnostics", "About"], wb.Worksheets.Select(ws => ws.Name));
        var about = wb.Worksheet("About").RowsUsed().ToDictionary(r => r.Cell(1).GetString(), r => r.Cell(2).Value);
        Assert.Equal("scheduling", about["Filter: group"].GetText());
        var expected = Diagnostics.Diagnose([], DateOnly.FromDateTime(Now.Date)).Where(c => c.Group == "scheduling").Select(c => c.Title);
        Assert.All(expected, title => Assert.True(about.ContainsKey(title), title));
        Assert.Equal((HttpStatusCode)422, (await client.GetAsync("/api/v1/diagnostics.xlsx?group=nope")).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await client.GetAsync("/api/v1/diagnostics.xlsx?check=nope")).StatusCode);
    }
}
