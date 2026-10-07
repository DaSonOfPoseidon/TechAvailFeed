using System.Net;
using ClosedXML.Excel;
using TechAvail.Core;
using TechAvail.Core.Parsing;
using TechAvail.Data;
using TechAvail.Data.Tests;

namespace TechAvail.Api.Tests;

public class ArrivalExportTests
{
    static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    static readonly DateOnly Day = new(2026, 10, 6);

    static XLWorkbook Open(byte[] data) => new(new MemoryStream(data));

    static ArrivalRow Row(string reference, string status, string state, string kind = "job") =>
        new("t", "T", kind, reference, status, "3", "North", Day.ToDateTime(new TimeOnly(8, 0)), null, null, null, null, state);

    [Fact]
    public void Highlighted_sheets_carry_state_rules_and_plain_ones_dont()
    {
        var at = Day.ToDateTime(new TimeOnly(8, 15));
        var data = ArrivalExport.Workbook(
            Chicago,
            DateTimeOffset.UtcNow,
            Day,
            null,
            [
                new("Arrivals 8 AM", at, [Row("a", "A", "not_started"), Row("e", "E", "en_route"), Row("o", "O", "not_started", "ticket")], true),
                new("Completed", at, [Row("c", "C", "arrived")], false),
            ]
        );
        using var wb = Open(data);
        var eight = wb.Worksheet("Arrivals 8 AM");
        Assert.Equal("State", eight.Cell(1, 13).GetText());
        Assert.Equal(["Not started", "En route", "Not started"], [.. Enumerable.Range(2, 3).Select(r => eight.Cell(r, 13).GetText())]);
        Assert.Equal(("Open", "Trouble Call"), (eight.Cell(4, 6).GetText(), eight.Cell(4, 3).GetText()));
        var rules = eight.ConditionalFormats.ToList();
        Assert.Equal(2, rules.Count);
        Assert.All(rules, r => Assert.Equal("A2:M4", r.Range.RangeAddress.ToString()));
        Assert.Equal(["$M2=\"Not started\"", "$M2=\"En route\""], rules.Select(r => r.Values[1].Value.TrimStart('=')));
        var completed = wb.Worksheet("Completed");
        Assert.Empty(completed.ConditionalFormats);
        Assert.Equal("Minutes en route", completed.Cell(1, completed.LastColumnUsed()!.ColumnNumber()).GetText());
        var headers = eight.Row(1).CellsUsed().Select(c => c.GetText().ToLowerInvariant()).ToList();
        Assert.DoesNotContain(headers, h => h is "latitude" or "longitude" or "gps precision");
    }

    // WCAG 2 relative luminance and contrast ratio.
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

    [Fact]
    public void Highlight_text_meets_wcag_aaa_contrast()
    {
        Assert.All(ArrivalExport.Highlights, h => Assert.True(Contrast(h.Fill, h.Font) >= 7, $"{h.State}: {Contrast(h.Fill, h.Font):F2}"));
        Assert.True(Contrast(Xlsx.Header.Fill, Xlsx.Header.Font) >= 7);
    }

    [Fact]
    public void Headers_are_purple_on_every_report_sheet()
    {
        var at = Day.ToDateTime(new TimeOnly(8, 15));
        using var wb = Open(
            ArrivalExport.Workbook(Chicago, DateTimeOffset.UtcNow, Day, null, [new("Arrivals 8 AM", at, [Row("a", "A", "not_started")], true), new("Completed", at, [], false)])
        );
        foreach (var (title, last) in new[] { ("Arrivals 8 AM", 13), ("Completed", 12) })
        {
            var style = wb.Worksheet(title).Cell(1, last).Style;
            Assert.Equal(XLColor.FromHtml("#7030A0"), style.Fill.BackgroundColor);
            Assert.Equal(XLColor.FromHtml("#FFFFFF"), style.Font.FontColor);
        }
    }

    [Fact]
    public void A_missing_815_run_is_said_on_its_sheet()
    {
        using var wb = Open(ArrivalExport.Workbook(Chicago, DateTimeOffset.UtcNow, Day, "North", [new("Arrivals 8 AM", null, [], true, "no 8:15 run on 2026-10-06")]));
        Assert.Equal("no 8:15 run on 2026-10-06", wb.Worksheet("Arrivals 8 AM").Cell(2, 1).GetText());
        var about = wb.Worksheet("About");
        Assert.Contains(about.RowsUsed(), r => r.Cell(1).GetText() == "Arrivals 8 AM: as of" && r.Cell(2).GetText() == "no snapshot");
        Assert.Contains(about.RowsUsed(), r => r.Cell(1).GetText() == "Filter: region" && r.Cell(2).GetText() == "North");
    }

    static Block B(string reference, string start, string status, string tech, string? enroute = null, string? inprogress = null) =>
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
            Region = "North",
            Skills = "",
            TaskType = "3",
            ModifiedAt = Day.ToDateTime(TimeOnly.Parse(start)),
            EnrouteAt = enroute is null ? null : Day.ToDateTime(TimeOnly.Parse(enroute)),
            InprogressAt = inprogress is null ? null : Day.ToDateTime(TimeOnly.Parse(inprogress)),
        };

    // 08:20 and 16:00 in Chicago.
    static readonly (DateTimeOffset At, Block[] Blocks)[] Runs =
    [
        (
            new(2026, 10, 6, 13, 20, 0, TimeSpan.Zero),
            [B("j1", "08:00", "A", "a"), B("j2", "08:00", "E", "b", enroute: "08:05"), B("j3", "15:00", "A", "c")]
        ),
        (
            new(2026, 10, 6, 21, 0, 0, TimeSpan.Zero),
            [
                B("j1", "08:00", "C", "a", enroute: "08:30", inprogress: "08:50"),
                B("j2", "08:00", "C", "b", enroute: "08:05", inprogress: "08:25"),
                B("j3", "15:00", "A", "c"),
            ]
        ),
    ];

    static TestDatabase Seed(int runs)
    {
        var db = new TestDatabase();
        var store = new FeedStore(db.ConnectionString);
        foreach (var (at, blocks) in Runs.Take(runs))
        {
            var feed = new ParsedFeed { Sha256 = $"x{at:HHmm}", Format = "blocks", GeneratedAt = at };
            feed.Blocks.AddRange(blocks);
            store.Save($"<{at:HHmm}>", "email", null, null, null, at, feed);
        }
        return db;
    }

    [DbFact]
    public async Task Today_has_the_815_view_and_the_day_so_far()
    {
        using var db = Seed(2);
        using var factory = new ApiFactory(db, new(2026, 10, 6, 21, 30, 0, TimeSpan.Zero));
        var response = await factory.CreateClient().GetAsync("/api/v1/arrivals.xlsx");
        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("attachment; filename=\"arrivals_2026-10-06.xlsx\"", response.Content.Headers.GetValues("Content-Disposition").Single());
        using var wb = Open(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(["Arrivals 8 AM", "Today so far", "About"], wb.Worksheets.Select(ws => ws.Name));
        var eight = wb.Worksheet("Arrivals 8 AM");
        Assert.Equal([("j1", "Not started"), ("j2", "En route")], Enumerable.Range(2, 2).Select(r => (eight.Cell(r, 4).GetText(), eight.Cell(r, 13).GetText())));
        var soFar = wb.Worksheet("Today so far");
        Assert.Equal(["j3", "j1", "j2"], Enumerable.Range(2, 3).Select(r => soFar.Cell(r, 4).GetText()));
    }

    [DbFact]
    public async Task A_past_day_lists_completed_jobs_first()
    {
        using var db = Seed(2);
        using var factory = new ApiFactory(db, new(2026, 10, 7, 15, 0, 0, TimeSpan.Zero));
        using var wb = Open(await factory.CreateClient().GetByteArrayAsync("/api/v1/arrivals.xlsx?date=2026-10-06"));
        Assert.Equal(["Completed", "Arrivals 8 AM", "About"], wb.Worksheets.Select(ws => ws.Name));
        var completed = wb.Worksheet("Completed");
        Assert.Equal([("j1", 50.0, 20.0), ("j2", 25.0, 20.0)], Enumerable.Range(2, 2).Select(r => (completed.Cell(r, 4).GetText(), completed.Cell(r, 11).GetDouble(), completed.Cell(r, 12).GetDouble())));
    }

    [DbFact]
    public async Task Before_the_815_run_and_future_days_are_refused()
    {
        using var db = Seed(0);
        using var factory = new ApiFactory(db, new(2026, 10, 6, 13, 0, 0, TimeSpan.Zero)); // 08:00 in Chicago
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/arrivals.xlsx")).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await client.GetAsync("/api/v1/arrivals.xlsx?date=2026-10-07")).StatusCode);
    }
}
