using ClosedXML.Excel;
using Column = TechAvail.Api.Xlsx.Column;
using TechAvail.Core;
using TechAvail.Core.Parsing;

namespace TechAvail.Api;

// Port of api/export.py: everything the dashboard shows, as one multi-sheet workbook. Cell values,
// number formats, widths, frozen headers and tables match the Python export. Job coordinates are
// never written.
public static class Export
{
    const string DateFormat = Xlsx.Date;
    const string TimeFormat = Xlsx.Time;

    static readonly (string Name, object? Text)[] Definitions =
    [
        .. CapacityExport.Definitions,
        .. OutcomesExport.Definitions,
    ];

    static readonly Column[] JeopardyColumns =
    [
        new("Tech"),
        new("Tech id"),
        new("Type"),
        new("Job #"),
        new("Task type"),
        new("Status"),
        new("Region"),
        new("Scheduled start", TimeFormat, 17),
        new("Scheduled end", TimeFormat, 17),
        new("JIJ at", TimeFormat, 17),
        new("Minutes past JIJ"),
    ];

    static object?[] JeopardyRow(JeopardyRow r) =>
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
        ];

    static object? Get(OrderedDictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) ? value : null;

    public static byte[] Workbook(
        TimeZoneInfo tz,
        DateTimeOffset now,
        long snapshotId,
        DateTimeOffset? generatedAt,
        OrderedDictionary<string, object?> filters,
        List<CalendarEntry> entries,
        List<TechDay> days,
        List<Block> demand,
        List<Block> schedule,
        List<JeopardyRow> jeopardy,
        List<(DayOutcome Outcome, bool Provisional)> outcomes,
        OrderedDictionary<string, object?> kpis,
        List<Check> checks
    )
    {
        using var wb = new XLWorkbook();
        CapacityExport.WriteSheets(wb, entries, days, demand, schedule);
        Xlsx.WriteSheet(wb, "Jobs in jeopardy", JeopardyColumns, [.. jeopardy.Select(JeopardyRow)]);
        OutcomesExport.WriteSheets(wb, outcomes, kpis, (string?)Get(filters, "region"), (string?)Get(filters, "tech"));
        DiagnosticsExport.WriteSheet(wb, checks);

        // Excel has no time zones: both times are written as local time, like the block timestamps.
        var about = wb.Worksheets.Add("About");
        var rowNumber = 0;
        void Append(object? name, object? value, bool bold = false)
        {
            rowNumber++;
            foreach (var (column, cellValue) in new[] { (1, name), (2, value) })
            {
                var cell = about.Cell(rowNumber, column);
                cell.Value = Xlsx.Cell(cellValue);
                if (cellValue is DateTime)
                    cell.Style.NumberFormat.Format = TimeFormat;
                else if (cellValue is DateOnly)
                    cell.Style.NumberFormat.Format = DateFormat;
                if (bold)
                    cell.Style.Font.Bold = true;
            }
        }
        var local = TimeZoneInfo.ConvertTime(now, tz).DateTime;
        Append("Exported at", local.AddTicks(-(local.Ticks % TimeSpan.TicksPerSecond)));
        Append("Snapshot id", snapshotId);
        Append("Snapshot generated at", generatedAt is { } g ? TimeZoneInfo.ConvertTime(g, tz).DateTime : null);
        foreach (var (name, value) in filters)
            Append($"Filter: {name}", value ?? "(all)");
        rowNumber++;
        Append("Metric", "Definition", bold: true);
        foreach (var (name, text) in Definitions)
            Append(name, text);
        rowNumber++;
        Append("Diagnostic", "Rows", bold: true);
        foreach (var (name, rows) in DiagnosticsExport.Counts(checks))
            Append(name, rows);
        // Last, so the rows above stay where the Python export has them.
        rowNumber++;
        Append(
            "JIJ",
            $"Job in jeopardy: today's job or trouble call that isn't completed by {Jeopardy.Lead.TotalMinutes:0} minutes before "
                + "its scheduled end (JIJ at), as of the snapshot."
        );
        Xlsx.SetWidth(about.Column(1), 24);
        Xlsx.SetWidth(about.Column(2), 100);

        return Xlsx.Save(wb);
    }
}
