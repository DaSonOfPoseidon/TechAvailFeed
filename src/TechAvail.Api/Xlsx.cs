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

    // Python's str() of a cell value, for column widths and diagnostic details.
    internal static string PyStr(object? value) =>
        value switch
        {
            null => "None",
            bool b => b ? "True" : "False",
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTime t => t.ToString(t.Ticks % TimeSpan.TicksPerSecond == 0 ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
            double x => Repr(x),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        };

    // repr() of a float: shortest round-trip digits, always with a decimal point or exponent.
    static string Repr(double x)
    {
        var text = x.ToString("R", CultureInfo.InvariantCulture);
        if (text.Contains('E'))
        {
            // 1E-05 -> 1e-05, 1E+20 -> 1e+20
            var parts = text.Split('E');
            var exponent = int.Parse(parts[1], CultureInfo.InvariantCulture);
            return $"{parts[0]}e{(exponent < 0 ? "-" : "+")}{Math.Abs(exponent):00}";
        }
        return text.Contains('.') || text.Contains("Infinity") || text == "NaN" ? text : text + ".0";
    }

    // The fixed width, else the longest of the header and the first 500 values (0, False and None count
    // as empty), between 8 and 50.
    static int Width(Column column, int index, List<object?[]> rows)
    {
        if (column.Width is { } width)
            return width;
        var longest = rows.Take(500)
            .Select(row => row[index] is null or false or 0 or 0.0 or "" ? 0 : PyStr(row[index]).Length)
            .Prepend(column.Header.Length)
            .Max();
        return Math.Min(Math.Max(longest + 2, 8), 50);
    }

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
