using System.Net;
using ClosedXML.Excel;
using TechAvail.Core;
using TechAvail.Data.Tests;

namespace TechAvail.Api.Tests;

public class OutcomesExportTests
{
    static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    static readonly DateOnly Day = new(2026, 10, 6);

    static XLWorkbook Open(byte[] data) => new(new MemoryStream(data));

    [Fact]
    public void A_day_without_a_morning_plan_gets_a_full_width_row()
    {
        var outcomes = new List<(DayOutcome, bool)> { (new DayOutcome(new DateOnly(2026, 9, 29), "no_morning"), false) };
        using var wb = Open(OutcomesExport.Workbook(Chicago, Now, Day.AddDays(-7), Day, null, null, outcomes));
        Assert.Equal(["Outcomes by day", "Outcome items", "About"], wb.Worksheets.Select(ws => ws.Name));
        var sheet = wb.Worksheet("Outcomes by day");
        var width = sheet.Row(1).CellsUsed().Count();
        Assert.Equal(new DateTime(2026, 9, 29), sheet.Cell(2, 1).GetDateTime());
        Assert.Equal(("no_morning", false), (sheet.Cell(2, 2).GetText(), sheet.Cell(2, 3).GetBoolean()));
        Assert.True(sheet.Cell(2, 4).IsEmpty());
        Assert.True(width > 4);
    }

    [DbFact]
    public async Task Outcomes_is_a_workbook_filtered_like_the_kpis()
    {
        using var db = new TestDatabase();
        CapacityExportTests.Seed(db);
        using var factory = new ApiFactory(db, Now);
        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/outcomes.xlsx?start=2026-10-06");
        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("attachment; filename=\"outcomes_2026-10-06.xlsx\"", response.Content.Headers.GetValues("Content-Disposition").Single());
        using var wb = Open(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("j1", wb.Worksheet("Outcome items").Cell(2, 4).GetText());
        var about = wb.Worksheet("About").RowsUsed().ToDictionary(r => r.Cell(1).GetString(), r => r.Cell(2).Value);
        Assert.Equal(new DateTime(2026, 10, 6), about["Filter: from"].GetDateTime());
        Assert.True(about.ContainsKey("Pulled d0"));
        Assert.False(about.ContainsKey("Net h"));

        using var other = Open(await client.GetByteArrayAsync("/api/v1/outcomes.xlsx?start=2026-10-06&tech=b"));
        Assert.Equal(1, other.Worksheet("Outcome items").LastRowUsed()!.RowNumber());
        Assert.Equal((HttpStatusCode)422, (await client.GetAsync("/api/v1/outcomes.xlsx?start=2026-10-07&end=2026-10-06")).StatusCode);
    }
}
