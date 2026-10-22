using System.Globalization;
using ClosedXML.Excel;
using TechAvail.Core;
using TechAvail.Core.Parsing;
using Column = TechAvail.Api.Xlsx.Column;

namespace TechAvail.Api;

// The next business days as a workbook: the VP's install availability (install calendar only), the
// techs left per booking window and area (MBSReporter's Q778), and the unassigned work behind it.
// Job coordinates are never written.
public static class CapacityExport
{
    public const string InstallSheet = "Install availability";
    public const string Full = "FULL";

    const double MaxRowHeight = 409;

    public static readonly IReadOnlyDictionary<string, (string Fill, string Font)> StatusColors = new Dictionary<string, (string, string)>
    {
        [InstallAvailability.Open] = Xlsx.Good,
        [InstallAvailability.Limited] = Xlsx.Neutral,
        [InstallAvailability.Full] = Xlsx.Bad,
        [InstallAvailability.NoTechs] = Xlsx.Bad,
        [InstallAvailability.Closed] = Xlsx.Muted,
    };

    static readonly (string Status, string Meaning)[] Legend =
    [
        (InstallAvailability.Open, $"{InstallAvailability.LimitedBelow:0%} or more of booking windows free"),
        (InstallAvailability.Limited, $"Under {InstallAvailability.LimitedBelow:0%} free"),
        (InstallAvailability.Full, "Shows 0% free: no booking windows left, or under half a percent"),
        (InstallAvailability.NoTechs, "The area's techs are all on time off (PTO)"),
        (InstallAvailability.Closed, "No techs scheduled, or not a booking day (Sunday)"),
    ];

    public const string AvailabilityNote =
        "Availability: the share of an area's 2-hour booking windows (8-10, 10-12, 1-3, 3-5, and 5-7 on weekdays) "
        + "that its working techs still have free to book.";

    internal static readonly (string Name, object? Text)[] Definitions =
    [
        (
            "Availability",
            "Install availability sheet: free booking windows / bookable windows (windows wholly inside a tech's "
                + "shift, minus lunch and time off, booked or not), per VP area. Today's only count if they start at "
                + "least 30 minutes after the export."
        ),
        (
            "Status",
            $"Open: {InstallAvailability.LimitedBelow:0%} or more, as shown (whole percent). Limited: under "
                + $"{InstallAvailability.LimitedBelow:0%}. Full: shows 0% (under half a percent free, or nothing left). No Techs: the area's "
                + "techs are all on time off. Closed: no tech scheduled, or no booking windows that day (Sunday)."
        ),
        (
            "Techs Available",
            "Summary sheet: techs with at least an hour free in the window, minus the unassigned jobs and tickets "
                + "that fall in it. FULL when nothing is left. Today's windows count from 30 minutes after the export."
        ),
        ("MDU Available", "A tech with the MDU skill has a free hour starting in the window (No when FULL)."),
        .. InstallAvailability.Areas.Select(a => ($"  {a.Area}", (object?)string.Join(", ", a.Territories))),
        ("  Other territories", "Rows of their own, listed when they have install techs in the sheet's days."),
    ];

    static readonly Column[] SummaryColumns =
    [
        new("Date", Xlsx.Date, 12),
        new("Area"),
        new("Timeframe"),
        new("Techs Available"),
        new("MDU Available"),
    ];

    static object?[] SummaryRow(WindowRow r) =>
        [
            r.Date,
            r.Area,
            r.Window.ToString("h tt", CultureInfo.InvariantCulture),
            r.Net > 0 ? r.Net : Full,
            r.Net > 0 && r.Mdu ? "Yes" : "No",
        ];

    static readonly Column[] BlockColumns =
    [
        new("Date", Xlsx.Date, 12),
        new("Kind"),
        new("Ref"),
        new("Status"),
        new("Task type"),
        new("Tech id"),
        new("Tech"),
        new("Region"),
        new("Starts", Xlsx.Time, 17),
        new("Ends", Xlsx.Time, 17),
        new("Address issue"),
    ];

    static object?[] BlockRow(Block b) =>
        [b.WorkDate, b.Kind, b.RefId, b.Status, b.TaskType, b.TechId, b.TechName, b.Region, b.StartsAt, b.EndsAt, b.AddressIssue];

    // Summary (FULL rows in red, no stripes so they stand out) and Unassigned work.
    static void WriteSheets(XLWorkbook wb, List<WindowRow> windows, List<Block> demand)
    {
        var summary = Xlsx.WriteSheet(wb, "Summary", SummaryColumns, [.. windows.Select(SummaryRow)], stripes: false);
        Xlsx.Highlight(summary, windows.Count, SummaryColumns.Length, 4, [(Full, Xlsx.Bad.Fill, Xlsx.Bad.Font)]);
        Xlsx.WriteSheet(wb, "Unassigned work", BlockColumns, [.. demand.Select(BlockRow)]);
    }

    // The VP's update laid out to paste into an email: title, the exceptions text in one wrapped cell (it
    // pastes as plain lines), then the colour-coded grid with its legend right under it, to copy as one block.
    static void WriteInstall(XLWorkbook wb, InstallGrid grid)
    {
        var ws = wb.Worksheets.Add(InstallSheet);
        ws.Cell(1, 1).Value = InstallAvailability.Title;
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;

        var columns = grid.Days.Count + 2;
        var text = ws.Range(3, 1, 3, columns).Merge();
        text.FirstCell().Value = string.Join("\n", grid.Exceptions);
        text.Style.Alignment.WrapText = true;
        text.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        // Excel caps a row at 409 points; a longer list stays whole in the cell (and pastes whole).
        ws.Row(3).Height = Math.Min(15 * grid.Exceptions.Count, MaxRowHeight);

        var row = 5;
        var header = row;
        Xlsx.HeaderRow(
            ws,
            row,
            [
                "Area",
                .. grid.Days.Select(d => d.ToString("ddd M/d", CultureInfo.InvariantCulture)),
                $"{grid.Days.Count}-Day Total",
            ]
        );
        foreach (var area in grid.Rows)
        {
            ws.Cell(++row, 1).Value = area.Area;
            var column = 1;
            foreach (var cell in area.Days.Append(area.Total))
            {
                var target = ws.Cell(row, ++column);
                if (cell.Share is { } share)
                {
                    target.Value = share;
                    target.Style.NumberFormat.Format = "0%";
                }
                else
                    target.Value = cell.Status;
                target.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                Xlsx.Fill(target, StatusColors[cell.Status]);
            }
        }
        Xlsx.Borders(ws, header, row, columns);

        ws.Cell(++row, 1).Value = "Legend";
        ws.Cell(row, 1).Style.Font.Bold = true;
        foreach (var (status, meaning) in Legend)
        {
            ws.Cell(++row, 1).Value = status;
            Xlsx.Fill(ws.Cell(row, 1), StatusColors[status]);
            ws.Cell(row, 2).Value = meaning;
        }
        ws.Cell(++row, 1).Value = AvailabilityNote;

        Xlsx.SetWidth(ws.Column(1), 28);
        for (var c = 2; c <= columns; c++)
            Xlsx.SetWidth(ws.Column(c), 12);
    }

    public static byte[] Workbook(
        TimeZoneInfo tz,
        DateTimeOffset now,
        long snapshotId,
        DateTimeOffset? generatedAt,
        OrderedDictionary<string, object?> filters,
        List<WindowRow> windows,
        List<Block> demand,
        InstallGrid? install = null
    )
    {
        using var wb = new XLWorkbook();
        if (install is not null)
            WriteInstall(wb, install);
        WriteSheets(wb, windows, demand);
        new AboutSheet(wb)
            .ExportedAt(tz, now)
            .Add("Snapshot id", snapshotId)
            .Add("Snapshot generated at", generatedAt is { } g ? TimeZoneInfo.ConvertTime(g, tz).DateTime : null)
            .Filters(filters)
            .Section("Metric", "Definition", Definitions);
        return Xlsx.Save(wb);
    }
}
