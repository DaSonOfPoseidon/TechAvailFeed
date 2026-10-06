using ClosedXML.Excel;
using TechAvail.Core;
using TechAvail.Core.Parsing;
using TechAvail.Data;
using TechAvail.Data.Tests;

namespace TechAvail.Api.Tests;

// Ports of tests/test_export.py and the export test in tests/test_api.py.
public class ExportTests
{
    static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    static XLWorkbook Open(byte[] data) => new(new MemoryStream(data));

    static List<List<XLCellValue>> Rows(IXLWorksheet ws) =>
        [.. ws.RowsUsed().Select(r => r.Cells(1, ws.LastColumnUsed()!.ColumnNumber()).Select(c => c.Value).ToList())];

    static byte[] Workbook(
        OrderedDictionary<string, object?>? filters = null,
        List<(DayOutcome, bool)>? outcomes = null,
        List<Check>? checks = null
    ) =>
        Export.Workbook(
            Chicago,
            Now,
            1,
            null,
            filters ?? [],
            [],
            [],
            [],
            [],
            outcomes ?? [],
            Kpis.OutcomeKpis(outcomes ?? []),
            checks ?? []
        );

    static Dictionary<string, XLCellValue> About(XLWorkbook wb) =>
        wb.Worksheet("About").RowsUsed().Where(r => !r.Cell(1).IsEmpty()).ToDictionary(r => r.Cell(1).GetString(), r => r.Cell(2).Value);

    [Fact]
    public void Headers_are_purple_with_white_bold_text()
    {
        using var wb = Open(Workbook());
        foreach (var ws in wb.Worksheets.Where(ws => ws.Name != "About"))
        {
            var style = ws.Cell(1, 1).Style;
            Assert.Equal((XLColor.FromHtml("#7030A0"), XLColor.FromHtml("#FFFFFF"), true), (style.Fill.BackgroundColor, style.Font.FontColor, style.Font.Bold));
        }
    }

    [Fact]
    public void Empty_export_still_opens_with_headers_and_no_tables()
    {
        using var wb = Open(Workbook(new() { ["region"] = "North" }));
        Assert.Equal(1, wb.Worksheet("Summary").LastRowUsed()!.RowNumber());
        Assert.Empty(wb.Worksheet("Summary").Tables);
        var about = About(wb);
        Assert.Equal("North", about["Filter: region"].GetText());
        Assert.True(about.ContainsKey("Net h"));
    }

    [Fact]
    public void Formula_like_values_are_stored_as_text()
    {
        var check = new Check("tech_no_region", "tech_setup", "No region", "warning", "")
        {
            Rows = [new() { ["tech_id"] = "=1+1", ["tech_name"] = "=HYPERLINK(\"http://x\",\"y\")" }],
        };
        using var wb = Open(Workbook(new() { ["region"] = "=cmd|' /C calc'!A0" }, checks: [check]));
        Assert.DoesNotContain(wb.Worksheets.SelectMany(ws => ws.CellsUsed()), c => c.HasFormula);
        var header = Rows(wb.Worksheet("Diagnostics"))[0].Select(v => v.ToString()).ToList();
        Assert.Equal("=1+1", Rows(wb.Worksheet("Diagnostics"))[1][header.IndexOf("Tech id")].GetText());
        Assert.Equal("=cmd|' /C calc'!A0", About(wb)["Filter: region"].GetText());
    }

    [Fact]
    public void A_day_without_a_morning_plan_gets_a_full_width_row()
    {
        using var wb = Open(Workbook(outcomes: [(new DayOutcome(new DateOnly(2026, 9, 29), "no_morning"), false)]));
        var sheet = wb.Worksheet("Outcomes by day");
        var width = sheet.Row(1).CellsUsed().Count();
        Assert.Equal(new DateTime(2026, 9, 29), sheet.Cell(2, 1).GetDateTime());
        Assert.Equal(("no_morning", false), (sheet.Cell(2, 2).GetText(), sheet.Cell(2, 3).GetBoolean()));
        Assert.True(sheet.Cell(2, 4).IsEmpty());
        Assert.True(width > 4);
    }

    [Fact]
    public void Diagnostics_sheet_flattens_check_specific_fields_into_detail()
    {
        var check = new Check("tech_no_region", "tech_setup", "No region", "warning", "")
        {
            Rows = [new() { ["tech_id"] = "a", ["tech_name"] = "A", ["days"] = 3, ["first_date"] = new DateOnly(2026, 10, 6) }],
        };
        using var wb = Open(Workbook(checks: [check]));
        var rows = Rows(wb.Worksheet("Diagnostics"));
        var header = rows[0].Select(v => v.ToString()).ToList();
        Assert.Equal("No region", rows[1][header.IndexOf("Check")].GetText());
        Assert.Equal("a", rows[1][header.IndexOf("Tech id")].GetText());
        Assert.Equal("days: 3; first date: 2026-10-06", rows[1][header.IndexOf("Detail")].GetText());
    }

    static Block B(string kind, string start, string end, string tech = "a", string region = "", string skills = "", string reference = "") =>
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

    [DbFact]
    public async Task Export_is_a_workbook()
    {
        using var db = new TestDatabase();
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
        using var factory = new ApiFactory(db, Now);
        var response = await factory.CreateClient().GetAsync("/api/v1/export.xlsx?days=2&history_days=1");
        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("attachment; filename=\"techavail_2026-10-06.xlsx\"", response.Content.Headers.GetValues("Content-Disposition").Single());
        using var wb = Open(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(
            ["Summary", "Tech days", "Free slots", "Schedule", "Unassigned work", "Outcomes by day", "Outcome items", "Diagnostics", "About"],
            wb.Worksheets.Select(ws => ws.Name)
        );
        var summary = wb.Worksheet("Summary");
        Assert.Equal(("Date", "Region", "Techs on"), (summary.Cell(1, 1).GetText(), summary.Cell(1, 2).GetText(), summary.Cell(1, 3).GetText()));
        Assert.Equal((new DateTime(2026, 10, 6), "(all)", 2.0), (summary.Cell(2, 1).GetDateTime(), summary.Cell(2, 2).GetText(), summary.Cell(2, 3).GetDouble()));
        Assert.Equal(1 + 3, wb.Worksheet("Schedule").LastRowUsed()!.RowNumber());
        Assert.Equal("j1", wb.Worksheet("Outcome items").Cell(2, 4).GetText());
        var headers = wb.Worksheets.SelectMany(ws => ws.Row(1).CellsUsed()).Select(c => c.GetString()).ToHashSet();
        Assert.DoesNotContain(headers, h => h.Contains("lat", StringComparison.OrdinalIgnoreCase) || h.Contains("lon", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Address issue", headers);
    }
}
