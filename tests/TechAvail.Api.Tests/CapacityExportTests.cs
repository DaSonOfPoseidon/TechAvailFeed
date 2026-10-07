using System.Net;
using ClosedXML.Excel;
using TechAvail.Core.Parsing;
using TechAvail.Data;
using TechAvail.Data.Tests;

namespace TechAvail.Api.Tests;

public class CapacityExportTests
{
    static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    static XLWorkbook Open(byte[] data) => new(new MemoryStream(data));

    static Dictionary<string, XLCellValue> About(XLWorkbook wb) =>
        wb.Worksheet("About").RowsUsed().Where(r => !r.Cell(1).IsEmpty()).ToDictionary(r => r.Cell(1).GetString(), r => r.Cell(2).Value);

    internal static Block B(string kind, string start, string end, string tech = "a", string region = "", string skills = "", string reference = "") =>
        new()
        {
            Kind = kind,
            WorkDate = new DateOnly(2026, 10, 6),
            TechId = tech,
            TechName = tech.ToUpperInvariant(),
            StartsAt = new DateOnly(2026, 10, 6).ToDateTime(TimeOnly.Parse(start)),
            EndsAt = new DateOnly(2026, 10, 6).ToDateTime(TimeOnly.Parse(end)),
            RefId = reference,
            Status = "A",
            Department = "FIELD",
            Region = region,
            Skills = skills,
            TaskType = kind == "job" ? "3" : "",
        };

    internal static void Seed(TestDatabase db)
    {
        var generated = new DateTimeOffset(2026, 10, 6, 11, 5, 0, TimeSpan.Zero);
        var feed = new ParsedFeed { Sha256 = "x", Format = "blocks", GeneratedAt = generated };
        feed.Blocks.AddRange(
            [
                B("shift", "08:00", "17:00", "a", "North", "INS, RECO"),
                B("shift", "08:00", "17:00", "b", "South", "INS"),
                B("job", "08:00", "10:00", "a", reference: "j1"),
                B("job_unassigned", "09:00", "11:00", "", "North", reference: "u1"),
            ]
        );
        new FeedStore(db.ConnectionString).Save("<1>", "email", null, null, null, generated, feed);
    }

    [Fact]
    public void Empty_workbook_still_opens_with_headers_and_capacity_definitions()
    {
        using var wb = Open(CapacityExport.Workbook(Chicago, Now, 1, null, new() { ["region"] = "North" }, [], [], [], []));
        Assert.Equal(["Summary", "Tech days", "Free slots", "Schedule", "Unassigned work", "About"], wb.Worksheets.Select(ws => ws.Name));
        Assert.Equal(1, wb.Worksheet("Summary").LastRowUsed()!.RowNumber());
        var about = About(wb);
        Assert.Equal("North", about["Filter: region"].GetText());
        Assert.True(about.ContainsKey("Net h"));
        Assert.False(about.ContainsKey("Planned"));
    }

    [DbFact]
    public async Task Capacity_is_a_workbook()
    {
        using var db = new TestDatabase();
        Seed(db);
        using var factory = new ApiFactory(db, Now);
        var response = await factory.CreateClient().GetAsync("/api/v1/capacity.xlsx?days=2");
        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("attachment; filename=\"capacity_2026-10-06.xlsx\"", response.Content.Headers.GetValues("Content-Disposition").Single());
        using var wb = Open(await response.Content.ReadAsByteArrayAsync());
        var summary = wb.Worksheet("Summary");
        Assert.Equal(("Date", "Region", "Techs on"), (summary.Cell(1, 1).GetText(), summary.Cell(1, 2).GetText(), summary.Cell(1, 3).GetText()));
        Assert.Equal((new DateTime(2026, 10, 6), "(all)", 2.0), (summary.Cell(2, 1).GetDateTime(), summary.Cell(2, 2).GetText(), summary.Cell(2, 3).GetDouble()));
        Assert.Equal(1 + 3, wb.Worksheet("Schedule").LastRowUsed()!.RowNumber());
        Assert.Equal("u1", wb.Worksheet("Unassigned work").Cell(2, 3).GetText());
        var headers = wb.Worksheets.SelectMany(ws => ws.Row(1).CellsUsed()).Select(c => c.GetString()).ToHashSet();
        Assert.DoesNotContain(headers, h => h.Contains("lat", StringComparison.OrdinalIgnoreCase) || h.Contains("lon", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Address issue", headers);
    }

    [DbFact]
    public async Task Bad_filters_are_rejected_like_the_calendar()
    {
        using var db = new TestDatabase();
        Seed(db);
        using var factory = new ApiFactory(db, Now);
        var client = factory.CreateClient();
        Assert.Equal((HttpStatusCode)422, (await client.GetAsync("/api/v1/capacity.xlsx?days=0")).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await client.GetAsync("/api/v1/capacity.xlsx?calendar=both")).StatusCode);
    }
}
