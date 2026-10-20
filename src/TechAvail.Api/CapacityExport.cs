using System.Globalization;
using ClosedXML.Excel;
using TechAvail.Core;
using TechAvail.Core.Parsing;
using Column = TechAvail.Api.Xlsx.Column;

namespace TechAvail.Api;

// The forward calendar as a workbook: the VP's install availability (install calendar only), capacity
// per day and region, each tech's day, the free slots, and the shifts, work and leave behind them.
// Job coordinates are never written.
public static class CapacityExport
{
    public const string InstallSheet = "Install availability";

    const double MaxRowHeight = 409;

    public static readonly IReadOnlyDictionary<string, (string Fill, string Font)> StatusColors = new Dictionary<string, (string, string)>
    {
        [InstallAvailability.Open] = Xlsx.Good,
        [InstallAvailability.Limited] = Xlsx.Neutral,
        [InstallAvailability.Closed] = Xlsx.Bad,
        [InstallAvailability.NoTechs] = Xlsx.Bad,
        [InstallAvailability.NotOpen] = Xlsx.Muted,
    };

    static readonly (string Status, string Meaning)[] Legend =
    [
        (InstallAvailability.Closed, $"Under {InstallAvailability.ClosedBelow:0%} of booking windows free (0%: all booked)"),
        (InstallAvailability.Limited, $"{InstallAvailability.ClosedBelow:0%} to under {InstallAvailability.LimitedBelow:0%} free"),
        (InstallAvailability.Open, $"{InstallAvailability.LimitedBelow:0%} or more free"),
        (InstallAvailability.NoTechs, "Techs scheduled, but all on time off"),
        (InstallAvailability.NotOpen, "No techs scheduled, or not a booking day (e.g. Sunday)"),
    ];

    internal static readonly (string Name, object? Text)[] Definitions =
    [
        ("Shift h", "Scheduled shift hours."),
        ("Available h", "Shift minus lunch (12-1; Saturdays before 10/17, 1-2) minus time off."),
        (
            "Booked h",
            "Jobs and tickets inside available time. Canceled/unnecessary tasks and "
                + "closed/deleted/held/cleared tickets don't count."
        ),
        ("Free h", "Open slots of at least 60 minutes; today's start no sooner than 30 minutes after the snapshot was read."),
        (
            "Free windows",
            "Booking windows (8-10, 10-12, 1-3, 3-5, and 5-7 on weekdays) wholly inside available time with no work "
                + "in them; today's only if they start at least 30 minutes after the snapshot was read."
        ),
        (
            "1h openings",
            "Windows holding one 1-hour job (voice install, equipment install or MDU) with the other hour free, "
                + "for techs skilled for a 1-hour job."
        ),
        ("Utilization", "Booked h / available h."),
        (
            "Unassigned h",
            "Scheduled jobs and tickets with no tech yet, in the region their address maps to. A skill filter keeps "
                + "only work needing that skill."
        ),
        ("Net h", "Free h minus unassigned h: capacity left once unassigned work is placed."),
        ("Techs off", "Techs with time off overlapping their shift, or a day off entirely."),
        (
            "Availability",
            "Install availability sheet: free booking windows / bookable windows (windows wholly inside available "
                + "time, booked or not), per VP area. Today's only count if they start at least 30 minutes after the export."
        ),
        (
            "Status",
            $"Closed: under {InstallAvailability.ClosedBelow:0%} as shown (whole percent), 0% when working techs have nothing left. "
                + $"Limited: under {InstallAvailability.LimitedBelow:0%}. Open: the rest. No Techs: the area's techs are all on time off. "
                + "Not Open: no tech scheduled, or no booking windows that day (Sunday)."
        ),
        .. InstallAvailability.Areas.Where(a => a.Territories is not [var t] || t != a.Area).Select(a => ($"  {a.Area}", (object?)string.Join(", ", a.Territories))),
        ("  Other territories", "Listed as rows of their own."),
    ];

    static readonly Column[] CapacityColumns =
    [
        new("Techs on"),
        new("Techs off"),
        new("Shift h", Xlsx.Hours),
        new("Available h", Xlsx.Hours),
        new("Booked h", Xlsx.Hours),
        new("Free h", Xlsx.Hours),
        new("Free windows"),
        new("1h openings"),
        new("Utilization", Xlsx.Percent),
        new("Jobs"),
        new("Tickets"),
        new("Unassigned jobs"),
        new("Unassigned tickets"),
        new("Unassigned h", Xlsx.Hours),
        new("Net h", Xlsx.Hours),
    ];

    static object?[] CapacityValues(Capacity c) =>
        [
            c.TechsOn,
            c.TechsOff,
            c.ShiftH,
            c.AvailableH,
            c.BookedH,
            c.FreeH,
            c.FreeSlots,
            c.FreeHourSlots,
            c.Utilization,
            c.Jobs,
            c.Tickets,
            c.UnassignedJobs,
            c.UnassignedTickets,
            c.UnassignedH,
            c.NetH,
        ];

    static readonly Column[] SummaryColumns = [new("Date", Xlsx.Date, 12), new("Region"), .. CapacityColumns];

    static readonly Column[] TechDayColumns =
    [
        new("Date", Xlsx.Date, 12),
        new("Tech id"),
        new("Tech"),
        new("Region"),
        new("Skills"),
        new("Shift h", Xlsx.Hours),
        new("Lunch h", Xlsx.Hours),
        new("Time off h", Xlsx.Hours),
        new("Available h", Xlsx.Hours),
        new("Booked h", Xlsx.Hours),
        new("Free h", Xlsx.Hours),
        new("Free windows"),
        new("1h openings"),
        new("Jobs"),
        new("Tickets"),
        new("On time off"),
    ];

    static readonly Column[] FreeSlotColumns =
    [
        new("Date", Xlsx.Date, 12),
        new("Tech id"),
        new("Tech"),
        new("Region"),
        new("Skills"),
        new("Open from", Xlsx.Time, 17),
        new("Open until", Xlsx.Time, 17),
        new("Minutes"),
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

    // Summary, Tech days, Free slots, Schedule and Unassigned work.
    internal static void WriteSheets(XLWorkbook wb, List<CalendarEntry> entries, List<TechDay> days, List<Block> demand, List<Block> schedule)
    {
        Xlsx.WriteSheet(
            wb,
            "Summary",
            SummaryColumns,
            [
                .. entries.SelectMany(entry =>
                    entry
                        .ByRegion.Select(r => (object?[])[entry.Date, r.Region.Length > 0 ? r.Region : "(none)", .. CapacityValues(r)])
                        .Prepend([entry.Date, "(all)", .. CapacityValues(entry.Totals)])
                ),
            ]
        );
        Xlsx.WriteSheet(
            wb,
            "Tech days",
            TechDayColumns,
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
                            d.FreeWindows,
                            d.HourOpenings,
                            d.Jobs,
                            d.Tickets,
                            d.OnTimeOff,
                        ]
                ),
            ]
        );
        Xlsx.WriteSheet(
            wb,
            "Free slots",
            FreeSlotColumns,
            [
                .. days.SelectMany(d =>
                    d.Free.Select(s => (object?[])[s.WorkDate, s.TechId, s.TechName, s.Region, s.Skills, s.OpenFrom, s.OpenUntil, s.OpenMinutes])
                ),
            ]
        );
        Xlsx.WriteSheet(wb, "Schedule", BlockColumns, [.. schedule.Select(BlockRow)]);
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
        ws.Cell(++row, 1).Value =
            "Availability = free 2-hour booking windows / bookable windows. Today counts only windows starting 30+ minutes after the export (About sheet).";

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
        List<CalendarEntry> entries,
        List<TechDay> days,
        List<Block> demand,
        List<Block> schedule,
        InstallGrid? install = null
    )
    {
        using var wb = new XLWorkbook();
        if (install is not null)
            WriteInstall(wb, install);
        WriteSheets(wb, entries, days, demand, schedule);
        new AboutSheet(wb)
            .ExportedAt(tz, now)
            .Add("Snapshot id", snapshotId)
            .Add("Snapshot generated at", generatedAt is { } g ? TimeZoneInfo.ConvertTime(g, tz).DateTime : null)
            .Filters(filters)
            .Section("Metric", "Definition", Definitions);
        return Xlsx.Save(wb);
    }
}
