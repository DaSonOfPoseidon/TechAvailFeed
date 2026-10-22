using ClosedXML.Excel;
using TechAvail.Core;

namespace TechAvail.Api;

// "Actions taking" is left blank for the dispatcher.
public static class JeopardyExport
{
    public const string UpdateSheet = "Update";
    public const string JeopardySheet = "Jobs in jeopardy";

    public static readonly (string Status, string Fill, string Font)[] Highlights =
    [
        ("Green", Xlsx.Good.Fill, Xlsx.Good.Font),
        ("Yellow", Xlsx.Neutral.Fill, Xlsx.Neutral.Font),
        ("Red", Xlsx.Bad.Fill, Xlsx.Bad.Font),
    ];

    static readonly Xlsx.Column[] Columns =
    [
        new("Tech"),
        new("Tech id"),
        new("Type"),
        new("Job #"),
        new("Task type"),
        new("Status"),
        new("Region"),
        new("Scheduled start", Xlsx.Time, 17),
        new("Scheduled end", Xlsx.Time, 17),
        new("JIJ at", Xlsx.Time, 17),
        new("Minutes past JIJ"),
        new("VP region"),
    ];

    static object?[] Values(JeopardyRow r) =>
        [
            r.Job.TechName,
            r.Job.TechId,
            Arrivals.KindNames[r.Job.Kind],
            r.Job.RefId,
            r.Job.TaskType,
            Arrivals.StatusNames[r.Job.Kind].GetValueOrDefault(r.Job.Status, r.Job.Status),
            r.Job.Region,
            r.Job.StartsAt,
            r.Job.EndsAt,
            r.JijAt,
            r.MinutesPast,
            StatusUpdate.RegionFor(r.Job.Region),
        ];

    public static string Title(TimeOnly? at, DateTime asOf) =>
        at is { } t ? $"{t.ToString(t.Minute == 0 ? "h tt" : "h:mm tt", System.Globalization.CultureInfo.InvariantCulture)} Update"
        : $"Update as of {asOf.ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture)}";

    static void WriteUpdate(XLWorkbook wb, string title, DateTime asOf, StatusReport report)
    {
        var ws = wb.Worksheets.Add(UpdateSheet);
        ws.Cell(1, 1).Value = title;
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Cell(2, 1).Value = "As of";
        ws.Cell(2, 2).Value = asOf;
        ws.Cell(2, 2).Style.NumberFormat.Format = Xlsx.Stamp;
        ws.Cell(2, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

        var row = 4;
        ws.Cell(row, 1).Value = "Tech Progress Overview by Region";
        ws.Cell(row, 1).Style.Font.Bold = true;
        Xlsx.HeaderRow(ws, ++row, "Region", "Status");
        var first = row + 1;
        foreach (var region in report.Regions)
        {
            row++;
            ws.Cell(row, 1).Value = region.Region;
            ws.Cell(row, 2).Value = region.Status;
            var (_, fill, font) = Highlights.Single(h => h.Status == region.Status);
            Xlsx.Fill(ws.Cell(row, 2), (fill, font));
        }
        Xlsx.Borders(ws, first - 1, row, 2);

        row += 2;
        Xlsx.HeaderRow(ws, row, "Areas/Techs of Concern", "Reason", "Actions Taking");
        first = row;
        foreach (var concern in report.Concerns)
        {
            row++;
            var area = concern.Area.Length > 0 ? concern.Area : "(no area)";
            ws.Cell(row, 1).Value = concern.TechName is { } tech ? $"{area} / {tech}" : area;
            ws.Cell(row, 2).Value = concern.Reason;
        }
        if (report.Concerns.Count == 0)
            ws.Cell(++row, 1).Value = "None";
        // A few spare rows for concerns the feed can't see (a blown tire, a sick call).
        row += 3;
        Xlsx.Borders(ws, first, row, 3);
        ws.Range(first + 1, 1, row, 3).Style.Alignment.WrapText = true;
        ws.Range(first + 1, 1, row, 3).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;

        Xlsx.SetWidth(ws.Column(1), 44);
        Xlsx.SetWidth(ws.Column(2), 48);
        Xlsx.SetWidth(ws.Column(3), 56);
    }

    public static byte[] Workbook(TimeZoneInfo tz, DateTimeOffset now, DateOnly day, TimeOnly? at, DateTime asOf, string? region, StatusReport report)
    {
        using var wb = new XLWorkbook();
        WriteUpdate(wb, Title(at, asOf), asOf, report);
        Xlsx.WriteSheet(wb, JeopardySheet, Columns, [.. report.Jeopardy.Select(Values)]);

        new AboutSheet(wb)
            .Add("Report day", day)
            .ExportedAt(tz, now)
            .Add("Snapshot", asOf)
            .Filters([new("region", region)])
            .Section(
                "Term",
                "Meaning",
                [
                    ("Region", "The VP region. Areas are the feed's region, grouped as MBSReporter's multiregion rules group them."),
                    .. StatusUpdate.Regions.Select(r => ($"  {r.Region}", (object?)string.Join(", ", r.Areas))),
                    ($"  {StatusUpdate.Unmapped}", "An area not in the list above; only shown when it has a concern."),
                    ("Status", $"Green: no concerns. Yellow: 1 to {StatusUpdate.YellowMax}. Red: more than {StatusUpdate.YellowMax}."),
                    (
                        "In jeopardy",
                        $"Today's job or trouble call that isn't completed by {Jeopardy.Lead.TotalMinutes:0} minutes before its scheduled end."
                    ),
                    ("Going long", "The tech's job is En Route or In Progress past its scheduled end, and their next job hasn't started."),
                    (
                        "Slot",
                        "A booking window (weekday 8, 10, 1, 3, 5; Saturday 8, 10, 1, 3; before 10/17, 9, 11, 2, 4) that isn't over, holding more open jobs than techs assigned to them."
                    ),
                    ("Actions Taking", "Left blank for the dispatcher. The spare rows are for concerns the feed can't see."),
                ]
            );
        return Xlsx.Save(wb);
    }
}
