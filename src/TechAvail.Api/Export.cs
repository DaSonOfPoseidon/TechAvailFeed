using System.Globalization;
using ClosedXML.Excel;
using TechAvail.Core;
using TechAvail.Core.Parsing;

namespace TechAvail.Api;

// Port of api/export.py: everything the dashboard shows, as one multi-sheet workbook. Cell values,
// number formats, widths, frozen headers and tables match the Python export. Job coordinates are
// never written.
public static class Export
{
    const string DateFormat = "yyyy-mm-dd";
    const string TimeFormat = "yyyy-mm-dd hh:mm";
    const string HoursFormat = "0.00";
    const string PercentFormat = "0.0%";

    static readonly (string Name, string Text)[] Definitions =
    [
        ("Shift h", "Scheduled shift hours."),
        ("Available h", "Shift minus lunch (12-1, Sat 1-2) minus time off."),
        (
            "Booked h",
            "Jobs and tickets inside available time. Canceled/unnecessary tasks and "
                + "closed/deleted/held/cleared tickets don't count."
        ),
        (
            "Free h",
            "Open slots of at least 60 minutes; today's start no sooner than 30 minutes "
                + "after the snapshot was read."
        ),
        ("Utilization", "Booked h / available h."),
        ("Unassigned h", "Scheduled jobs and tickets with no tech yet, in the region their address maps to."),
        ("Net h", "Free h minus unassigned h: capacity left once unassigned work is placed."),
        ("Techs off", "Techs with time off overlapping their shift, or a day off entirely."),
        (
            "Planned",
            "Install jobs and FIELD/TC tickets assigned for the day in the first snapshot "
                + "between 06:00 and 07:00."
        ),
        (
            "d0 / d1 / d2",
            "The last snapshot before midnight ending the plan day, the next day, " + "and the day after."
        ),
        ("Outcome", "The latest checkpoint's result: d2 for final days, so far for provisional."),
        (
            "Pulled d0",
            "Planned jobs canceled, unscheduled or rescheduled by the end of the plan day, "
                + "split by how far the tech got (in progress, en route)."
        ),
        ("Provisional", "D+2 hasn't ended yet, so the day's outcomes can still change."),
    ];

    public sealed record Column(string Header, string? Format = null, int? Width = null);

    static readonly Column[] CapacityColumns =
    [
        new("Techs on"),
        new("Techs off"),
        new("Shift h", HoursFormat),
        new("Available h", HoursFormat),
        new("Booked h", HoursFormat),
        new("Free h", HoursFormat),
        new("Utilization", PercentFormat),
        new("Jobs"),
        new("Tickets"),
        new("Unassigned jobs"),
        new("Unassigned tickets"),
        new("Unassigned h", HoursFormat),
        new("Net h", HoursFormat),
    ];

    static object?[] CapacityValues(Capacity c) =>
        [
            c.TechsOn,
            c.TechsOff,
            c.ShiftH,
            c.AvailableH,
            c.BookedH,
            c.FreeH,
            c.Utilization,
            c.Jobs,
            c.Tickets,
            c.UnassignedJobs,
            c.UnassignedTickets,
            c.UnassignedH,
            c.NetH,
        ];

    static readonly Column[] OutcomeDayColumns =
    [
        new("Date", DateFormat, 12),
        new("Status"),
        new("Provisional"),
        new("Kind"),
        new("Planned"),
        .. Outcomes.Checkpoints.Select(c => new Column($"Completed {c}")),
        .. Outcomes.Checkpoints.Select(c => new Column($"Completion {c}", PercentFormat)),
        .. Outcomes.OutcomeNames.Select(o => new Column(Capitalize(o.Replace('_', ' ')))),
        new("Pulled d0"),
        .. Outcomes.ReachedNames.Select(r => new Column($"Pulled {r.Replace('_', ' ')}")),
        new("Pulled, prereqs open"),
    ];

    static readonly Column[] DiagnosticColumns =
    [
        new("Check"),
        new("Severity"),
        new("Ref"),
        new("Kind"),
        new("Status"),
        new("Date", DateFormat, 12),
        new("Starts", TimeFormat, 17),
        new("Ends", TimeFormat, 17),
        new("Tech id"),
        new("Tech"),
        new("Region"),
        new("Detail", Width: 60),
    ];

    static readonly string[] DiagnosticFields = ["ref_id", "kind", "status", "work_date", "starts_at", "ends_at", "tech_id", "tech_name", "region"];

    static readonly Column[] BlockColumns =
    [
        new("Date", DateFormat, 12),
        new("Kind"),
        new("Ref"),
        new("Status"),
        new("Task type"),
        new("Tech id"),
        new("Tech"),
        new("Region"),
        new("Starts", TimeFormat, 17),
        new("Ends", TimeFormat, 17),
        new("Address issue"),
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

    // str.capitalize(): first character upper, the rest lower.
    static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant();

    // Python's str() of a cell value, for column widths and diagnostic details.
    static string PyStr(object? value) =>
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

    // The width Python computes: str(value or "") of the first 500 rows, so 0, False and None count as "".
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

    // ClosedXML adds this padding to every width it writes; taking it off stores the width the
    // Python export stores, so columns look the same in Excel.
    const double WidthPadding = 0.710625;

    internal static void SetWidth(IXLColumn column, double width) => column.Width = width - WidthPadding;

    internal static XLCellValue Cell(object? value) =>
        value switch
        {
            // openpyxl never writes an empty string; the cell stays blank.
            null or "" => Blank.Value,
            string s => s,
            bool b => b,
            int n => n,
            long n => n,
            double x => x,
            DateOnly d => d.ToDateTime(TimeOnly.MinValue),
            DateTime t => t,
            _ => throw new InvalidOperationException($"unexpected {value.GetType()} in the export"),
        };

    // Every workbook's header: Excel's standard purple under white bold text (8.0:1, WCAG AAA). The
    // grey row stripes keep black text at well over 7:1. api/export.py uses the same colours.
    public static readonly (string Fill, string Font) Header = ("#7030A0", "#FFFFFF");

    internal static void WriteSheet(XLWorkbook wb, string title, IReadOnlyList<Column> columns, List<object?[]> rows)
    {
        var ws = wb.Worksheets.Add(title);
        for (int c = 0; c < columns.Count; c++)
            ws.Cell(1, c + 1).Value = columns[c].Header;
        var header = ws.Row(1).Cells(1, columns.Count).Style;
        header.Fill.SetBackgroundColor(XLColor.FromHtml(Header.Fill));
        header.Font.SetFontColor(XLColor.FromHtml(Header.Font));
        header.Font.SetBold(true);
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
    }

    static object? Get(OrderedDictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) ? value : null;

    static List<object?[]> OutcomeDayRows(OrderedDictionary<string, object?> kpis)
    {
        var rows = new List<object?[]>();
        foreach (var entry in (List<OrderedDictionary<string, object?>>)kpis["days"]!)
        {
            if ((string)entry["status"]! != "ok")
            {
                var row = new object?[OutcomeDayColumns.Length];
                (row[0], row[1], row[2]) = (entry["date"], entry["status"], entry["provisional"]);
                rows.Add(row);
                continue;
            }
            foreach (var kind in Outcomes.Kinds)
            {
                var stats = (OrderedDictionary<string, object?>)entry[kind]!;
                var completed = (OrderedDictionary<string, int>)stats["completed"]!;
                var rates = (OrderedDictionary<string, double?>)stats["completion_rate"]!;
                var outcome = (OrderedDictionary<string, int>)stats["outcome"]!;
                var pulled = stats.TryGetValue("pulled_d0", out var p) ? (OrderedDictionary<string, int>)p! : null;
                rows.Add(
                    [
                        entry["date"],
                        entry["status"],
                        entry["provisional"],
                        kind,
                        stats["planned"],
                        .. Outcomes.Checkpoints.Select(c => (object?)completed[c]),
                        .. Outcomes.Checkpoints.Select(c => (object?)rates[c]),
                        .. Outcomes.OutcomeNames.Select(o => (object?)outcome[o]),
                        pulled?["total"],
                        .. Outcomes.ReachedNames.Select(r => (object?)pulled?[r]),
                        pulled?["prereqs_open"],
                    ]
                );
            }
        }
        return rows;
    }

    // Fixed columns for the job or tech, and whatever else a check reports as "name: value".
    static List<object?[]> DiagnosticRows(IEnumerable<Check> checks) =>
    [
        .. checks.SelectMany(check =>
            check.Rows.Select(row =>
            {
                var detail = string.Join(
                    "; ",
                    row.Where(f => !DiagnosticFields.Contains(f.Key)).Select(f => $"{f.Key.Replace('_', ' ')}: {PyStr(f.Value)}")
                );
                return (object?[])[check.Title, check.Severity, .. DiagnosticFields.Select(f => Get(row, f)), detail];
            })
        ),
    ];

    static object?[] BlockRow(Block b) =>
        [b.WorkDate, b.Kind, b.RefId, b.Status, b.TaskType, b.TechId, b.TechName, b.Region, b.StartsAt, b.EndsAt, b.AddressIssue];

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
        WriteSheet(
            wb,
            "Summary",
            [new("Date", DateFormat, 12), new("Region"), .. CapacityColumns],
            [
                .. entries.SelectMany(entry =>
                    entry
                        .ByRegion.Select(r => (object?[])[entry.Date, r.Region.Length > 0 ? r.Region : "(none)", .. CapacityValues(r)])
                        .Prepend([entry.Date, "(all)", .. CapacityValues(entry.Totals)])
                ),
            ]
        );
        WriteSheet(
            wb,
            "Tech days",
            [
                new("Date", DateFormat, 12),
                new("Tech id"),
                new("Tech"),
                new("Region"),
                new("Skills"),
                new("Shift h", HoursFormat),
                new("Lunch h", HoursFormat),
                new("Time off h", HoursFormat),
                new("Available h", HoursFormat),
                new("Booked h", HoursFormat),
                new("Free h", HoursFormat),
                new("Jobs"),
                new("Tickets"),
                new("On time off"),
            ],
            [
                .. days.Select(d =>
                    (object?[])
                        [
                            d.WorkDate,
                            d.TechId,
                            d.TechName,
                            d.Region,
                            d.Skills,
                            d.ShiftHours,
                            d.LunchHours,
                            d.TimeOffHours,
                            d.AvailableHours,
                            d.BookedHours,
                            d.FreeHours,
                            d.Jobs,
                            d.Tickets,
                            d.OnTimeOff,
                        ]
                ),
            ]
        );
        WriteSheet(
            wb,
            "Free slots",
            [
                new("Date", DateFormat, 12),
                new("Tech id"),
                new("Tech"),
                new("Region"),
                new("Skills"),
                new("Open from", TimeFormat, 17),
                new("Open until", TimeFormat, 17),
                new("Minutes"),
            ],
            [
                .. days.SelectMany(d =>
                    d.Free.Select(s => (object?[])[s.WorkDate, s.TechId, s.TechName, s.Region, s.Skills, s.OpenFrom, s.OpenUntil, s.OpenMinutes])
                ),
            ]
        );
        WriteSheet(wb, "Schedule", BlockColumns, [.. schedule.Select(BlockRow)]);
        WriteSheet(wb, "Unassigned work", BlockColumns, [.. demand.Select(BlockRow)]);
        WriteSheet(wb, "Jobs in jeopardy", JeopardyColumns, [.. jeopardy.Select(JeopardyRow)]);
        WriteSheet(wb, "Outcomes by day", OutcomeDayColumns, OutcomeDayRows(kpis));
        var region = (string?)Get(filters, "region");
        var tech = (string?)Get(filters, "tech");
        WriteSheet(
            wb,
            "Outcome items",
            [
                new("Plan date", DateFormat, 12),
                new("Provisional"),
                new("Kind"),
                new("Ref"),
                new("Tech id"),
                new("Tech"),
                new("Region"),
                new("Task type"),
                new("Planned start", TimeFormat, 17),
                new("d0"),
                new("d1"),
                new("d2"),
                new("Reached"),
                new("Prereqs open"),
            ],
            [
                .. outcomes
                    .OrderBy(o => o.Outcome.Day)
                    .SelectMany(o =>
                        o.Outcome.Items.Where(i => (region is null || i.Region == region) && (tech is null || i.TechId == tech))
                            .Select(i =>
                                (object?[])
                                    [
                                        o.Outcome.Day,
                                        o.Provisional,
                                        i.Kind,
                                        i.RefId,
                                        i.TechId,
                                        i.TechName,
                                        i.Region,
                                        i.TaskType,
                                        i.PlannedStart,
                                        i.Outcomes.GetValueOrDefault("d0"),
                                        i.Outcomes.GetValueOrDefault("d1"),
                                        i.Outcomes.GetValueOrDefault("d2"),
                                        i.Reached,
                                        i.PrereqsOpen,
                                    ]
                            )
                    ),
            ]
        );
        WriteSheet(wb, "Diagnostics", DiagnosticColumns, DiagnosticRows(checks));

        // Excel has no time zones: both times are written as local time, like the block timestamps.
        var about = wb.Worksheets.Add("About");
        var rowNumber = 0;
        void Append(object? name, object? value, bool bold = false)
        {
            rowNumber++;
            foreach (var (column, cellValue) in new[] { (1, name), (2, value) })
            {
                var cell = about.Cell(rowNumber, column);
                cell.Value = Cell(cellValue);
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
        foreach (var check in checks)
            Append(check.Title, check.Available ? check.Rows.Count : "not in feed yet");
        // Last, so the rows above stay where the Python export has them.
        rowNumber++;
        Append(
            "JIJ",
            $"Job in jeopardy: today's job or trouble call that isn't completed by {Jeopardy.Lead.TotalMinutes:0} minutes before "
                + "its scheduled end (JIJ at), as of the snapshot."
        );
        SetWidth(about.Column(1), 24);
        SetWidth(about.Column(2), 100);

        using var buffer = new MemoryStream();
        wb.SaveAs(buffer);
        return buffer.ToArray();
    }
}
