using ClosedXML.Excel;
using TechAvail.Core;

namespace TechAvail.Api;

// One sheet of the arrival workbook. Missing replaces the rows when the snapshot never came.
public sealed record ArrivalSheet(string Title, DateTime? AsOf, List<ArrivalRow> Rows, bool Highlight, string? Missing = null);

// The arrival report as a workbook. Highlighting is Excel conditional formatting on the State
// column, so it follows the rows when they're sorted or filtered. Job coordinates are never written.
public static class ArrivalExport
{
    const string TimeFormat = "h:mm AM/PM";
    const string StampFormat = "yyyy-mm-dd h:mm AM/PM";

    // Excel's built-in "light red fill with dark red text" and yellow equivalents.
    static readonly (string State, string Fill, string Font)[] Highlights =
    [
        ("Not started", "#FFC7CE", "#9C0006"),
        ("En route", "#FFEB9C", "#9C5700"),
    ];

    static readonly Dictionary<string, string> StateNames = new()
    {
        ["not_started"] = "Not started",
        ["en_route"] = "En route",
        ["arrived"] = "Arrived",
    };

    static readonly Export.Column[] Columns =
    [
        new("Tech"),
        new("Tech id"),
        new("Type"),
        new("Job #"),
        new("Task type"),
        new("Status"),
        new("Region"),
        new("Scheduled start", TimeFormat, 16),
        new("En route", TimeFormat, 11),
        new("In progress", TimeFormat, 13),
        new("Minutes late"),
        new("Minutes en route"),
    ];

    static readonly Export.Column StateColumn = new("State");

    static object?[] Values(ArrivalRow r) =>
        [
            r.TechName,
            r.TechId,
            Arrivals.KindNames[r.Kind],
            r.RefId,
            r.TaskType,
            Arrivals.StatusNames[r.Kind].GetValueOrDefault(r.Status, r.Status),
            r.Region,
            r.ScheduledStart,
            r.EnrouteAt,
            r.InprogressAt,
            r.MinutesLate,
            r.MinutesEnRoute,
        ];

    static void WriteSheet(XLWorkbook wb, ArrivalSheet sheet)
    {
        Export.Column[] columns = sheet.Highlight ? [.. Columns, StateColumn] : Columns;
        List<object?[]> rows =
        [
            .. sheet.Rows.Select(r => sheet.Highlight ? [.. Values(r), StateNames.GetValueOrDefault(r.State, "")] : Values(r)),
        ];
        Export.WriteSheet(wb, sheet.Title, columns, rows);
        var ws = wb.Worksheet(sheet.Title);
        if (sheet.Missing is { } missing)
            ws.Cell(2, 1).Value = missing;
        if (!sheet.Highlight || rows.Count == 0)
            return;
        var state = XLHelper.GetColumnLetterFromNumber(columns.Length);
        var range = ws.Range(2, 1, rows.Count + 1, columns.Length);
        foreach (var (name, fill, font) in Highlights)
        {
            var style = range.AddConditionalFormat().WhenIsTrue($"${state}2=\"{name}\"");
            style.Fill.SetBackgroundColor(XLColor.FromHtml(fill));
            style.Font.SetFontColor(XLColor.FromHtml(font));
        }
    }

    public static byte[] Workbook(TimeZoneInfo tz, DateTimeOffset now, DateOnly day, string? region, IReadOnlyList<ArrivalSheet> sheets)
    {
        using var wb = new XLWorkbook();
        foreach (var sheet in sheets)
            WriteSheet(wb, sheet);

        var about = wb.Worksheets.Add("About");
        var rowNumber = 0;
        void Append(object? name, object? value, string? format = null, bool bold = false)
        {
            rowNumber++;
            foreach (var (column, cellValue) in new[] { (1, name), (2, value) })
            {
                var cell = about.Cell(rowNumber, column);
                cell.Value = Export.Cell(cellValue);
                if (format is not null && column == 2)
                    cell.Style.NumberFormat.Format = format;
                if (bold)
                    cell.Style.Font.Bold = true;
            }
        }
        var local = TimeZoneInfo.ConvertTime(now, tz).DateTime;
        Append("Report day", day, "yyyy-mm-dd");
        Append("Exported at", local.AddTicks(-(local.Ticks % TimeSpan.TicksPerSecond)), StampFormat);
        Append("Filter: region", region ?? "(all)");
        foreach (var sheet in sheets)
            Append($"{sheet.Title}: as of", sheet.AsOf is { } at ? at : "no snapshot", StampFormat);
        rowNumber++;
        Append("Column", "Meaning", bold: true);
        Append(
            "Arrivals 8 AM",
            "Each tech with an 8:00 job, as of the 8:15 run. In progress is the tech's first In Progress mark that day "
                + "on any job (techs swap job order), so it can be earlier than 8:00."
        );
        Append("Today so far", "Every job completed today, plus every job whose window has already started.");
        Append("Completed", "Every job each tech completed that day, as of the day's last snapshot.");
        Append("Minutes late", "In progress minus the scheduled start. Negative is early.");
        Append("Minutes en route", "In progress minus En route; for a tech still on the way, the snapshot time minus En route.");
        Append("Red", "Not started: the job is still Active (task) or Open (trouble call).");
        Append("Yellow", "En route: no In Progress mark yet, but En Route.");
        Append("Trouble calls", "The feed carries no En route / In progress times for trouble calls, so those cells are blank.");
        Export.SetWidth(about.Column(1), 24);
        Export.SetWidth(about.Column(2), 100);

        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        return stream.ToArray();
    }
}
