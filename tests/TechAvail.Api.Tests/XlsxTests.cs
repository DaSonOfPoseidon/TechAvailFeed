using ClosedXML.Excel;

namespace TechAvail.Api.Tests;

public class XlsxTests
{
    static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    static XLWorkbook Build(Action<XLWorkbook> write)
    {
        using var wb = new XLWorkbook();
        write(wb);
        return new XLWorkbook(new MemoryStream(Xlsx.Save(wb)));
    }

    [Fact]
    public void Sheet_has_a_purple_bold_frozen_header_and_a_striped_table()
    {
        using var wb = Build(wb => Xlsx.WriteSheet(wb, "Some rows", [new("Name"), new("Day", Xlsx.Date, 12)], [["a", new DateOnly(2026, 10, 7)]]));
        var ws = wb.Worksheet("Some rows");
        var style = ws.Cell(1, 1).Style;
        Assert.Equal((XLColor.FromHtml(Xlsx.Header.Fill), XLColor.FromHtml(Xlsx.Header.Font), true), (style.Fill.BackgroundColor, style.Font.FontColor, style.Font.Bold));
        Assert.Equal(1, ws.SheetView.SplitRow);
        Assert.Equal(("Somerows", "A1:B2"), (ws.Tables.Single().Name, ws.Tables.Single().RangeAddress.ToString()));
        Assert.Equal((new DateTime(2026, 10, 7), Xlsx.Date), (ws.Cell(2, 2).GetDateTime(), ws.Cell(2, 2).Style.NumberFormat.Format));
    }

    [Fact]
    public void Empty_sheet_keeps_its_header_and_no_table()
    {
        using var wb = Build(wb => Xlsx.WriteSheet(wb, "Empty", [new("Name")], []));
        var ws = wb.Worksheet("Empty");
        Assert.Equal(("Name", 1), (ws.Cell(1, 1).GetText(), ws.LastRowUsed()!.RowNumber()));
        Assert.Empty(ws.Tables);
    }

    [Fact]
    public void Formula_like_values_are_stored_as_text()
    {
        using var wb = Build(wb =>
        {
            Xlsx.WriteSheet(wb, "Rows", [new("Tech id")], [["=1+1"]]);
            new AboutSheet(wb).Filters([new("region", "=cmd|' /C calc'!A0")]);
        });
        Assert.DoesNotContain(wb.Worksheets.SelectMany(ws => ws.CellsUsed()), c => c.HasFormula);
        Assert.Equal("=1+1", wb.Worksheet("Rows").Cell(2, 1).GetText());
        Assert.Equal("=cmd|' /C calc'!A0", wb.Worksheet("About").Cell(1, 2).GetText());
    }

    [Fact]
    public void Highlight_colours_rows_by_the_key_column()
    {
        using var wb = Build(wb =>
        {
            var ws = Xlsx.WriteSheet(wb, "Rows", [new("Name"), new("State")], [["a", "Late"], ["b", "Fine"]]);
            Xlsx.Highlight(ws, 2, 2, 2, [("Late", "#FFC7CE", "#830005")]);
        });
        var rule = wb.Worksheet("Rows").ConditionalFormats.Single();
        Assert.Equal(("A2:B3", "$B2=\"Late\""), (rule.Range.RangeAddress.ToString(), rule.Values[1].Value.TrimStart('=')));
    }

    [Fact]
    public void About_formats_by_type_and_bolds_section_headings()
    {
        var now = new DateTimeOffset(2026, 10, 7, 15, 30, 12, 500, TimeSpan.Zero);
        using var wb = Build(wb =>
            new AboutSheet(wb)
                .Add("Report day", new DateOnly(2026, 10, 7))
                .ExportedAt(Chicago, now)
                .Filters([new("region", null)])
                .Section("Term", "Meaning", [("Free h", "Open slots.")])
        );
        var ws = wb.Worksheet("About");
        Assert.Equal(Xlsx.Date, ws.Cell(1, 2).Style.NumberFormat.Format);
        Assert.Equal((new DateTime(2026, 10, 7, 10, 30, 12), Xlsx.Stamp), (ws.Cell(2, 2).GetDateTime(), ws.Cell(2, 2).Style.NumberFormat.Format));
        Assert.Equal(("Filter: region", "(all)"), (ws.Cell(3, 1).GetText(), ws.Cell(3, 2).GetText()));
        Assert.True(ws.Cell(4, 1).IsEmpty());
        Assert.True(ws.Cell(5, 1).Style.Font.Bold && ws.Cell(5, 2).Style.Font.Bold);
        Assert.Equal("Free h", ws.Cell(6, 1).GetText());
        Assert.False(ws.Cell(6, 1).Style.Font.Bold);
    }
}
