using ClosedXML.Excel;
using TechAvail.Core;

namespace TechAvail.Api;

// One sheet of the arrival workbook. Missing replaces the rows when the snapshot never came.
public sealed record ArrivalSheet(string Title, DateTime? AsOf, List<ArrivalRow> Rows, bool Highlight, string? Missing = null);

// The arrival report as a workbook. Highlighting is Excel conditional formatting on the State
// column, so it follows the rows when they're sorted or filtered. Job coordinates are never written.
public static class ArrivalExport
{
    // Excel's built-in light red and yellow fills, with the text darkened from Excel's to reach WCAG
    // AAA contrast (at least 7:1; 7.3 for both).
    public static readonly (string State, string Fill, string Font)[] Highlights =
    [
        ("Not started", "#FFC7CE", "#830005"),
        ("En route", "#FFEB9C", "#703F00"),
    ];

    static readonly Dictionary<string, string> StateNames = new()
    {
        ["not_started"] = "Not started",
        ["en_route"] = "En route",
        ["arrived"] = "Arrived",
    };

    static readonly Xlsx.Column[] Columns =
    [
        new("Tech"),
        new("Tech id"),
        new("Type"),
        new("Job #"),
        new("Task type"),
        new("Status"),
        new("Region"),
        new("Scheduled start", Xlsx.Clock, 16),
        new("En route", Xlsx.Clock, 11),
        new("In progress", Xlsx.Clock, 13),
        new("Minutes late"),
        new("Minutes en route"),
    ];

    static readonly Xlsx.Column StateColumn = new("State");

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
        Xlsx.Column[] columns = sheet.Highlight ? [.. Columns, StateColumn] : Columns;
        List<object?[]> rows =
        [
            .. sheet.Rows.Select(r => sheet.Highlight ? [.. Values(r), StateNames.GetValueOrDefault(r.State, "")] : Values(r)),
        ];
        var ws = Xlsx.WriteSheet(wb, sheet.Title, columns, rows);
        if (sheet.Missing is { } missing)
            ws.Cell(2, 1).Value = missing;
        if (sheet.Highlight)
            Xlsx.Highlight(ws, rows.Count, columns.Length, columns.Length, Highlights);
    }

    public static byte[] Workbook(TimeZoneInfo tz, DateTimeOffset now, DateOnly day, string? region, IReadOnlyList<ArrivalSheet> sheets)
    {
        using var wb = new XLWorkbook();
        foreach (var sheet in sheets)
            WriteSheet(wb, sheet);

        var about = new AboutSheet(wb).Add("Report day", day).ExportedAt(tz, now).Filters([new("region", region)]);
        foreach (var sheet in sheets)
            about.Add($"{sheet.Title}: as of", sheet.AsOf is { } at ? at : "no snapshot");
        about.Section(
            "Column",
            "Meaning",
            [
                (
                    "Arrivals 8 AM",
                    "Each tech with an 8:00 job, as of the 8:15 run. In progress is the tech's first In Progress mark that day "
                        + "on any job (techs swap job order), so it can be earlier than 8:00."
                ),
                ("Today so far", "Every job completed today, plus every job whose window has already started."),
                ("Completed", "Every job each tech completed that day, as of the day's last snapshot."),
                ("Minutes late", "In progress minus the scheduled start. Negative is early."),
                ("Minutes en route", "In progress minus En route; for a tech still on the way, the snapshot time minus En route."),
                ("Red", "Not started: the job is still Active (task) or Open (trouble call)."),
                ("Yellow", "En route: no In Progress mark yet, but En Route."),
                ("Trouble calls", "The feed carries no En route / In progress times for trouble calls, so those cells are blank."),
            ]
        );
        return Xlsx.Save(wb);
    }
}
