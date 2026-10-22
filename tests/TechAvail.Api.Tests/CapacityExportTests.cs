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
    public void Empty_workbook_still_opens_with_headers_and_definitions()
    {
        using var wb = Open(CapacityExport.Workbook(Chicago, Now, 1, null, new() { ["region"] = "North" }, [], []));
        Assert.Equal(["Summary", "Unassigned work", "About"], wb.Worksheets.Select(ws => ws.Name));
        Assert.Equal(1, wb.Worksheet("Summary").LastRowUsed()!.RowNumber());
        var about = About(wb);
        Assert.Equal("North", about["Filter: region"].GetText());
        Assert.True(about.ContainsKey("Techs Available"));
        Assert.False(about.ContainsKey("Net h"));
    }

    [DbFact]
    public async Task Capacity_is_a_workbook()
    {
        using var db = new TestDatabase();
        Seed(db);
        using var factory = new ApiFactory(db, Now);
        var response = await factory.CreateClient().GetAsync("/api/v1/capacity.xlsx");
        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("attachment; filename=\"capacity_2026-10-06.xlsx\"", response.Content.Headers.GetValues("Content-Disposition").Single());
        using var wb = Open(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal([CapacityExport.InstallSheet, "Summary", "Unassigned work", "About"], wb.Worksheets.Select(ws => ws.Name));
        var summary = wb.Worksheet("Summary");
        Assert.Equal(
            ["Date", "Area", "Timeframe", "Techs Available", "MDU Available"],
            summary.Row(1).CellsUsed().Select(c => c.GetString())
        );
        // North's 8 AM: a's job fills it and u1 needs a tech too. South's b is free all morning.
        Assert.Equal(
            (new DateTime(2026, 10, 6), "North", "8 AM", "FULL", "No"),
            (summary.Cell(2, 1).GetDateTime(), summary.Cell(2, 2).GetText(), summary.Cell(2, 3).GetText(), summary.Cell(2, 4).GetText(), summary.Cell(2, 5).GetText())
        );
        var south = summary.RowsUsed().First(r => r.Cell(2).GetString() == "South");
        Assert.Equal(("8 AM", 1.0), (south.Cell(3).GetText(), south.Cell(4).GetDouble()));
        Assert.False(summary.Tables.Single().ShowRowStripes);
        Assert.Equal(new DateTime(2026, 10, 10), About(wb)["Filter: to"].GetDateTime());
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
            [new DateOnly(2026, 10, 16), new DateOnly(2026, 10, 17), new DateOnly(2026, 10, 18)],
            new DateOnly(2026, 10, 16)
        );
        using var wb = Open(CapacityExport.Workbook(Chicago, Now, 1, null, [], [], [], grid));
        Assert.Equal(CapacityExport.InstallSheet, wb.Worksheets.First().Name);
        var ws = wb.Worksheet(CapacityExport.InstallSheet);
        Assert.Equal(InstallAvailability.Title, ws.Cell(1, 1).GetText());
        // No As-of row (the About sheet has the export time); the exceptions are one wrapped cell.
        Assert.True(ws.Row(2).IsEmpty());
        var text = ws.Cell(3, 1);
        Assert.True(text.IsMerged() && text.Style.Alignment.WrapText);
        Assert.Equal(string.Join("\n", grid.Exceptions), text.GetText());
        // Carrollton has no techs on Saturday; 10% on Friday is Limited, not an exception.
        Assert.Contains("Carrollton - No Techs", grid.Exceptions);
        Assert.DoesNotContain("Carrollton - Limited", grid.Exceptions);

        var header = ws.Column(1).CellsUsed().Single(c => c.GetString() == "Area").Address.RowNumber;
        Assert.Equal(["Area", "Fri 10/16", "Sat 10/17", "Sun 10/18", "3-Day Total"], ws.Row(header).CellsUsed().Select(c => c.GetString()));
        var central = ws.Row(header + 2);
        Assert.Equal("Central MO", central.Cell(1).GetText());
        Assert.Equal(0.2, central.Cell(2).GetDouble());
        Assert.Equal(XLColor.FromHtml(CapacityExport.StatusColors[InstallAvailability.Limited].Fill), central.Cell(2).Style.Fill.BackgroundColor);
        Assert.Equal(XLColor.FromHtml(CapacityExport.StatusColors[InstallAvailability.Open].Fill), central.Cell(3).Style.Fill.BackgroundColor);
        Assert.Equal(InstallAvailability.Closed, central.Cell(4).GetText());
        Assert.Equal(InstallAvailability.NoTechs, ws.Row(header + 1).Cell(3).GetText());

        // The legend follows the table directly, so the two copy as one block.
        var last = header + grid.Rows.Count;
        Assert.Equal("Legend", ws.Cell(last + 1, 1).GetText());
        Assert.Equal(
            [InstallAvailability.Open, InstallAvailability.Limited, InstallAvailability.Full, InstallAvailability.NoTechs, InstallAvailability.Closed],
            Enumerable.Range(last + 2, 5).Select(r => ws.Cell(r, 1).GetText())
        );
        Assert.Equal(CapacityExport.AvailabilityNote, ws.Cell(last + 7, 1).GetText());
        Assert.DoesNotContain("About", CapacityExport.AvailabilityNote);
    }

    [Fact]
    public void A_long_exceptions_list_stays_within_excels_row_height()
    {
        // Every area plus extra territories unavailable on both days: more lines than a row can show.
        List<TechDay> days =
        [
            .. Enumerable.Range(0, 30).Select(i => new TechDay(new DateOnly(2026, 10, 16), $"t{i}", "T", $"Territory {i}", "INS", [], [], [], [], 0, 0, 0, 0, 0, 0, 0, 0, true, 0, 0, 0)),
        ];
        var grid = InstallAvailability.Build(days, [new DateOnly(2026, 10, 16), new DateOnly(2026, 10, 17)], new DateOnly(2026, 10, 16));
        Assert.True(grid.Exceptions.Count * 15 > 409);
        using var wb = Open(CapacityExport.Workbook(Chicago, Now, 1, null, [], [], [], grid));
        var ws = wb.Worksheet(CapacityExport.InstallSheet);
        Assert.Equal(string.Join("\n", grid.Exceptions), ws.Cell(3, 1).GetText());
        Assert.True(ws.Row(3).Height <= 409);
    }

    [DbFact]
    public async Task Unassigned_work_is_listed_once_and_not_for_a_skipped_sunday()
    {
        using var db = new TestDatabase();
        var generated = new DateTimeOffset(2026, 10, 6, 11, 5, 0, TimeSpan.Zero);
        var feed = new ParsedFeed { Sha256 = "x", Format = "blocks", GeneratedAt = generated };
        static Block On(DateOnly day, string reference) =>
            B("job_unassigned", "09:00", "11:00", "", "North", reference: reference) with
            {
                WorkDate = day,
                StartsAt = day.ToDateTime(new TimeOnly(9, 0)),
                EndsAt = day.ToDateTime(new TimeOnly(11, 0)),
            };
        // From Saturday 10/10, the window is 10/10 and 10/12-10/15: nobody works Sunday 10/11.
        // "sat" is sent twice; it is one piece of work.
        feed.Blocks.AddRange([On(new DateOnly(2026, 10, 10), "sat"), On(new DateOnly(2026, 10, 10), "sat"), On(new DateOnly(2026, 10, 11), "sun")]);
        new FeedStore(db.ConnectionString).Save("<1>", "email", null, null, null, generated, feed);
        using var factory = new ApiFactory(db, Now);
        var response = await factory.CreateClient().GetAsync("/api/v1/capacity.xlsx?start=2026-10-10");
        using var wb = Open(await response.Content.ReadAsByteArrayAsync());
        var refs = wb.Worksheet("Unassigned work").Column(3).CellsUsed().Skip(1).Select(c => c.GetString()).ToList();
        Assert.Equal(["sat"], refs);
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
        Assert.Equal((HttpStatusCode)422, (await client.GetAsync("/api/v1/capacity.xlsx?calendar=both")).StatusCode);
    }
}
