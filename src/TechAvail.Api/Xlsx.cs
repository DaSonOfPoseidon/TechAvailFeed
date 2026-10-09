using System.Globalization;
using ClosedXML.Excel;

namespace TechAvail.Api;

// The building blocks every workbook shares: column definitions, cell values, the purple header, table
// sheets, highlights and saving. A new export is its columns, its rows and an AboutSheet.
public static class Xlsx
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public const string Date = "yyyy-mm-dd";
    public const string Time = "yyyy-mm-dd hh:mm";
    public const string Clock = "h:mm AM/PM";
    public const string Stamp = "yyyy-mm-dd h:mm AM/PM";
    public const string Hours = "0.00";
    public const string Percent = "0.0%";

    public sealed record Column(string Header, string? Format = null, int? Width = null);

    // Every workbook's header: Excel's standard purple under white bold text (8.0:1, WCAG AAA). The
    // grey row stripes keep black text at well over 7:1.
    public static readonly (string Fill, string Font) Header = ("#7030A0", "#FFFFFF");

    // A cell value as text, for column widths and diagnostic details.
    internal static string Text(object? value) =>
        value switch
        {
            null => "",
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTime t => t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        };

    // The fixed width, else the longest of the header and the first 500 values, between 8 and 50.
    static int Width(Column column, int index, List<object?[]> rows) =>
        column.Width
        ?? Math.Clamp(rows.Take(500).Select(row => Text(row[index]).Length).Prepend(column.Header.Length).Max() + 2, 8, 50);

    // ClosedXML adds this padding to every width it writes; taking it off stores the width asked for.
    const double WidthPadding = 0.710625;

    public static void SetWidth(IXLColumn column, double width) => column.Width = width - WidthPadding;

    public static XLCellValue Cell(object? value) =>
        value switch
        {
            // An empty string stays a blank cell.
            null or "" => Blank.Value,
            string s => s,
            bool b => b,
            int n => n,
            long n => n,
            double x => x,
            DateOnly d => d.ToDateTime(TimeOnly.MinValue),
            DateTime t => t,
            _ => throw new InvalidOperationException($"unexpected {value.GetType()} in a workbook"),
        };

    // A header row in the shared purple, for sheets laid out by hand.
    public static void HeaderRow(IXLWorksheet ws, int row, params string[] headers)
    {
        for (var c = 0; c < headers.Length; c++)
            ws.Cell(row, c + 1).Value = headers[c];
        var style = ws.Range(row, 1, row, headers.Length).Style;
        style.Fill.SetBackgroundColor(XLColor.FromHtml(Header.Fill));
        style.Font.SetFontColor(XLColor.FromHtml(Header.Font));
        style.Font.SetBold(true);
    }

    public static void Borders(IXLWorksheet ws, int fromRow, int toRow, int columns)
    {
        var range = ws.Range(fromRow, 1, toRow, columns);
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
    }

    // One sheet as an Excel table: header, typed cells, number formats, widths and a frozen header row.
    public static IXLWorksheet WriteSheet(XLWorkbook wb, string title, IReadOnlyList<Column> columns, List<object?[]> rows)
    {
        var ws = wb.Worksheets.Add(title);
        HeaderRow(ws, 1, [.. columns.Select(c => c.Header)]);
        for (int r = 0; r < rows.Count; r++)
            for (int c = 0; c < columns.Count; c++)
                ws.Cell(r + 2, c + 1).Value = Cell(rows[r][c]);
        for (int c = 0; c < columns.Count; c++)
        {
            if (columns[c].Format is { } format && rows.Count > 0)
                ws.Range(2, c + 1, rows.Count + 1, c + 1).Style.NumberFormat.Format = format;
            SetWidth(ws.Column(c + 1), Width(columns[c], c, rows));
        }
        ws.SheetView.FreezeRows(1);
        if (rows.Count > 0)
        {
            // Excel rejects a table with no data rows; an empty sheet keeps just its header.
            var table = ws.Range(1, 1, rows.Count + 1, columns.Count).CreateTable(title.Replace(" ", ""));
            table.Theme = XLTableTheme.TableStyleLight1;
            table.ShowRowStripes = true;
        }
        return ws;
    }

    // Colours whole data rows by the text in one column. It is conditional formatting, so the colours
    // follow the rows when they're sorted or filtered.
    public static void Highlight(IXLWorksheet ws, int rows, int columns, int keyColumn, IEnumerable<(string Value, string Fill, string Font)> highlights)
    {
        if (rows == 0)
            return;
        var key = XLHelper.GetColumnLetterFromNumber(keyColumn);
        var range = ws.Range(2, 1, rows + 1, columns);
        foreach (var (value, fill, font) in highlights)
        {
            var style = range.AddConditionalFormat().WhenIsTrue($"${key}2=\"{value}\"");
            style.Fill.SetBackgroundColor(XLColor.FromHtml(fill));
            style.Font.SetFontColor(XLColor.FromHtml(font));
        }
    }

    public static byte[] Save(XLWorkbook wb)
    {
        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        return stream.ToArray();
    }
}
