using ClosedXML.Excel;

namespace TechAvail.Api;

// The "About" sheet that ends every workbook: name/value rows (when it was exported, which snapshot,
// the filters) and bold-headed sections of definitions. Excel has no time zones, so times are local.
public sealed class AboutSheet
{
    readonly IXLWorksheet ws;
    int row;

    public AboutSheet(XLWorkbook wb)
    {
        ws = wb.Worksheets.Add("About");
        Xlsx.SetWidth(ws.Column(1), 24);
        Xlsx.SetWidth(ws.Column(2), 100);
    }

    // A date gets the date format and a time the stamp format, unless format says otherwise.
    public AboutSheet Add(object? name, object? value, string? format = null, bool bold = false)
    {
        row++;
        foreach (var (column, cellValue) in new[] { (1, name), (2, value) })
        {
            var cell = ws.Cell(row, column);
            cell.Value = Xlsx.Cell(cellValue);
            var numberFormat =
                (column == 2 ? format : null)
                ?? cellValue switch
                {
                    DateTime => Xlsx.Stamp,
                    DateOnly => Xlsx.Date,
                    _ => null,
                };
            if (numberFormat is not null)
                cell.Style.NumberFormat.Format = numberFormat;
            if (bold)
                cell.Style.Font.Bold = true;
        }
        return this;
    }

    // The wall-clock time of the export in local time, to the second.
    public AboutSheet ExportedAt(TimeZoneInfo tz, DateTimeOffset now, string? format = null)
    {
        var local = TimeZoneInfo.ConvertTime(now, tz).DateTime;
        return Add("Exported at", local.AddTicks(-(local.Ticks % TimeSpan.TicksPerSecond)), format);
    }

    // "Filter: name" rows; an unset filter reads "(all)".
    public AboutSheet Filters(IEnumerable<KeyValuePair<string, object?>> filters)
    {
        foreach (var (name, value) in filters)
            Add($"Filter: {name}", value ?? "(all)");
        return this;
    }

    // A blank row, a bold heading row, then the rows.
    public AboutSheet Section(string name, string value, IEnumerable<(string Name, object? Value)> rows)
    {
        row++;
        Add(name, value, bold: true);
        foreach (var (rowName, rowValue) in rows)
            Add(rowName, rowValue);
        return this;
    }
}
