using System.Net;
using ClosedXML.Excel;
using TechAvail.Core;
using TechAvail.Core.Parsing;
using TechAvail.Data;
using TechAvail.Data.Tests;

namespace TechAvail.Api.Tests;

public class JeopardyExportTests
{
    static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    static readonly DateOnly Day = new(2026, 10, 6);

    static XLWorkbook Open(byte[] data) => new(new MemoryStream(data));

    static Block B(string reference, string start, string status, string tech, string area = "Hannibal-Bowling Green") =>
        new()
        {
            Kind = "job",
            WorkDate = Day,
            TechId = tech,
            TechName = tech.ToUpperInvariant(),
            StartsAt = Day.ToDateTime(TimeOnly.Parse(start)),
            EndsAt = Day.ToDateTime(TimeOnly.Parse(start)).AddHours(2),
            RefId = reference,
            Status = status,
            Department = "FIELD",
            Region = area,
            Skills = "",
            TaskType = "3",
            Latitude = 39.7,
            Longitude = -91.4,
        };

    static (string Region, string Status)[] Regions(IXLWorksheet ws) =>
        [.. Enumerable.Range(6, StatusUpdate.Regions.Length).Select(r => (ws.Cell(r, 1).GetText(), ws.Cell(r, 2).GetText()))];

    [Fact]
    public void The_update_sheet_has_both_tables_with_status_fills_and_blank_actions()
    {
        var asOf = Day.ToDateTime(new TimeOnly(10, 0, 51));
        var report = StatusUpdate.Build([B("1", "08:00", "I", "a"), B("2", "10:00", "A", "a"), B("3", "08:00", "A", "b", "Carrollton")], Day, asOf);
        using var wb = Open(JeopardyExport.Workbook(Chicago, DateTimeOffset.UtcNow, Day, new TimeOnly(10, 0), asOf, null, report));
        Assert.Equal(["Update", "Jobs in jeopardy", "About"], wb.Worksheets.Select(ws => ws.Name));
        var ws = wb.Worksheet("Update");
        Assert.Equal("10 AM Update", ws.Cell(1, 1).GetText());
        Assert.Equal(("Region", "Status"), (ws.Cell(5, 1).GetText(), ws.Cell(5, 2).GetText()));
        var regions = Regions(ws);
        Assert.Contains(("STL West", "Yellow"), regions);
        Assert.Contains(("West", "Yellow"), regions);
        Assert.Contains(("COMO", "Green"), regions);
        foreach (var (status, fill, font) in JeopardyExport.Highlights.Where(h => h.Status != "Red"))
        {
            var cell = ws.Column(2).CellsUsed().First(c => c.GetFormattedString() == status);
            Assert.Equal((XLColor.FromHtml(fill), XLColor.FromHtml(font)), (cell.Style.Fill.BackgroundColor, cell.Style.Font.FontColor));
        }

        var header = ws.RowsUsed().First(r => r.Cell(1).GetFormattedString() == "Areas/Techs of Concern").RowNumber();
        Assert.Equal(("Reason", "Actions Taking"), (ws.Cell(header, 2).GetText(), ws.Cell(header, 3).GetText()));
        Assert.Equal(XLColor.FromHtml(Xlsx.Header.Fill), ws.Cell(header, 3).Style.Fill.BackgroundColor);
        Assert.Equal(
            [("Hannibal-Bowling Green / A", "8AM job going long – 10AM in jeopardy"), ("Carrollton / B", "8AM job in jeopardy")],
            Enumerable.Range(header + 1, 2).Select(r => (ws.Cell(r, 1).GetText(), ws.Cell(r, 2).GetText()))
        );
        Assert.True(ws.Cell(header + 1, 3).IsEmpty());

        var detail = wb.Worksheet("Jobs in jeopardy");
        Assert.Equal("VP region", detail.Cell(1, detail.LastColumnUsed()!.ColumnNumber()).GetText());
        var headers = detail.Row(1).CellsUsed().Select(c => c.GetText().ToLowerInvariant()).ToList();
        Assert.DoesNotContain(headers, h => h is "latitude" or "longitude" or "gps precision");
        Assert.DoesNotContain(wb.Worksheets.SelectMany(s => s.CellsUsed()), c => c.GetFormattedString() is "39.7" or "-91.4");
    }

    [Fact]
    public void Status_text_meets_wcag_aaa_contrast()
    {
        static double Luminance(string hex)
        {
            double Channel(int i)
            {
                var c = Convert.ToInt32(hex.Substring(i, 2), 16) / 255.0;
                return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
        }
        static double Contrast(string a, string b)
        {
            var (x, y) = (Luminance(a), Luminance(b));
            return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
        }
        Assert.All(JeopardyExport.Highlights, h => Assert.True(Contrast(h.Fill, h.Font) >= 7, $"{h.Status}: {Contrast(h.Fill, h.Font):F2}"));
    }

    [Fact]
    public void Without_a_time_the_title_names_the_snapshot()
    {
        Assert.Equal("Update as of 10:47 AM", JeopardyExport.Title(null, Day.ToDateTime(new TimeOnly(10, 47))));
        Assert.Equal("1 PM Update", JeopardyExport.Title(new TimeOnly(13, 0), Day.ToDateTime(new TimeOnly(13, 0, 50))));
    }

    // 09:45, 10:00 and 10:15 in Chicago: the 8AM job turns from on time to running long.
    static readonly (DateTimeOffset At, Block[] Blocks)[] Runs =
    [
        (new(2026, 10, 6, 14, 45, 40, TimeSpan.Zero), [B("1", "08:00", "I", "a"), B("2", "10:00", "A", "a")]),
        (new(2026, 10, 6, 15, 0, 51, TimeSpan.Zero), [B("1", "08:00", "I", "a"), B("2", "10:00", "A", "a"), B("3", "10:00", "A", "c", "St Louis West")]),
        (new(2026, 10, 6, 15, 15, 41, TimeSpan.Zero), [B("1", "08:00", "C", "a"), B("2", "10:00", "I", "a")]),
    ];

    static TestDatabase Seed()
    {
        var db = new TestDatabase();
        var store = new FeedStore(db.ConnectionString);
        foreach (var (at, blocks) in Runs)
        {
            var feed = new ParsedFeed { Sha256 = $"x{at:HHmmss}", Format = "blocks", GeneratedAt = at };
            feed.Blocks.AddRange(blocks);
            store.Save($"<{at:HHmmss}>", "email", null, null, null, at, feed);
        }
        return db;
    }

    [DbFact]
    public async Task At_ten_reads_the_ten_oclock_run()
    {
        using var db = Seed();
        using var factory = new ApiFactory(db, new(2026, 10, 6, 21, 0, 0, TimeSpan.Zero));
        var response = await factory.CreateClient().GetAsync("/api/v1/jeopardy.xlsx?at=10:00");
        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("attachment; filename=\"jeopardy_2026-10-06_1000.xlsx\"", response.Content.Headers.GetValues("Content-Disposition").Single());
        using var wb = Open(await response.Content.ReadAsByteArrayAsync());
        var ws = wb.Worksheet("Update");
        Assert.Equal("10 AM Update", ws.Cell(1, 1).GetText());
        Assert.Contains(("STL West", "Yellow"), Regions(ws));
        Assert.Contains(ws.CellsUsed(), c => c.GetFormattedString() == "8AM job going long – 10AM in jeopardy");
    }

    [DbFact]
    public async Task Without_a_time_it_reads_the_days_latest_run_and_filters_by_region()
    {
        using var db = Seed();
        using var factory = new ApiFactory(db, new(2026, 10, 6, 21, 0, 0, TimeSpan.Zero));
        using var wb = Open(await factory.CreateClient().GetByteArrayAsync("/api/v1/jeopardy.xlsx?region=STL%20West"));
        var ws = wb.Worksheet("Update");
        Assert.Equal("Update as of 10:15 AM", ws.Cell(1, 1).GetText());
        Assert.Equal(("STL West", "Green"), (ws.Cell(6, 1).GetText(), ws.Cell(6, 2).GetText()));
        Assert.True(ws.Cell(7, 1).IsEmpty());
    }

    [DbFact]
    public async Task Future_times_unknown_regions_and_days_without_runs_are_refused()
    {
        using var db = Seed();
        using var factory = new ApiFactory(db, new(2026, 10, 6, 15, 30, 0, TimeSpan.Zero)); // 10:30 in Chicago
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/jeopardy.xlsx?at=13:00")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/jeopardy.xlsx?at=09:00")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/jeopardy.xlsx?date=2026-10-05")).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await client.GetAsync("/api/v1/jeopardy.xlsx?region=Hannibal")).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await client.GetAsync("/api/v1/jeopardy.xlsx?date=2026-10-07")).StatusCode);
    }
}
