using System.Net;
using ClosedXML.Excel;
using TechAvail.Core;
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

    [Fact]
    public void Install_availability_is_the_first_sheet_laid_out_for_the_email()
    {
        static TechDay Day(int date, string region, int free, int bookable) =>
            new(new DateOnly(2026, 10, date), "t", "T", region, "INS", [], [], [], [], 0, 0, 0, 0, 0, 0, 0, 0, false, free, 0, bookable);

        var grid = InstallAvailability.Build(
            [Day(16, "Carrollton", 1, 10), Day(16, "Columbia Core", 2, 10), Day(17, "Columbia Core", 5, 10)],
            new DateOnly(2026, 10, 16),
            days: 3,
            allAreas: false
        );
        using var wb = Open(CapacityExport.Workbook(Chicago, Now, 1, null, [], [], [], [], [], grid));
        Assert.Equal(CapacityExport.InstallSheet, wb.Worksheets.First().Name);
        var ws = wb.Worksheet(CapacityExport.InstallSheet);
        Assert.Equal(InstallAvailability.Title, ws.Cell(1, 1).GetText());
        var texts = ws.Column(1).CellsUsed().Select(c => c.GetString()).ToList();
        Assert.Contains("Friday 10/16", texts);
        Assert.Contains("Carrollton - Closed", texts);
        Assert.Contains("Saturday 10/17", texts);

        var header = ws.Column(1).CellsUsed().Single(c => c.GetString() == "Area").Address.RowNumber;
        Assert.Equal(["Area", "Fri 10/16", "Sat 10/17", "Sun 10/18", "3-Day Total"], ws.Row(header).CellsUsed().Select(c => c.GetString()));
        var central = ws.Row(header + 2);
        Assert.Equal("Central MO", central.Cell(1).GetText());
        Assert.Equal(0.2, central.Cell(2).GetDouble());
        Assert.Equal(XLColor.FromHtml(CapacityExport.StatusColors[InstallAvailability.Limited].Fill), central.Cell(2).Style.Fill.BackgroundColor);
        Assert.Equal(XLColor.FromHtml(CapacityExport.StatusColors[InstallAvailability.Open].Fill), central.Cell(3).Style.Fill.BackgroundColor);
        Assert.Equal("No slots", central.Cell(4).GetText());
        Assert.Equal("Closed", ws.Row(header + 1).Cell(3).GetText());
    }

    [DbFact]
    public async Task Only_the_install_calendar_gets_the_availability_sheet()
    {
        using var db = new TestDatabase();
        Seed(db);
        using var factory = new ApiFactory(db, Now);
        var client = factory.CreateClient();
        using var install = Open(await (await client.GetAsync("/api/v1/capacity.xlsx")).Content.ReadAsByteArrayAsync());
        Assert.Equal(CapacityExport.InstallSheet, install.Worksheets.First().Name);
        using var tc = Open(await (await client.GetAsync("/api/v1/capacity.xlsx?calendar=tc")).Content.ReadAsByteArrayAsync());
        Assert.DoesNotContain(tc.Worksheets, ws => ws.Name == CapacityExport.InstallSheet);
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
