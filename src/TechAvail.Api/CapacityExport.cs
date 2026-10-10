using ClosedXML.Excel;
using TechAvail.Core;
using TechAvail.Core.Parsing;
using Column = TechAvail.Api.Xlsx.Column;

namespace TechAvail.Api;

// The forward calendar as a workbook: capacity per day and region, each tech's day, the free slots,
// and the shifts, work and leave behind them. Job coordinates are never written.
public static class CapacityExport
{
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
        ("Utilization", "Booked h / available h."),
        (
            "Unassigned h",
            "Scheduled jobs and tickets with no tech yet, in the region their address maps to. A skill filter keeps "
                + "only work needing that skill."
        ),
        ("Net h", "Free h minus unassigned h: capacity left once unassigned work is placed."),
        ("Techs off", "Techs with time off overlapping their shift, or a day off entirely."),
    ];

    static readonly Column[] CapacityColumns =
    [
        new("Techs on"),
        new("Techs off"),
        new("Shift h", Xlsx.Hours),
        new("Available h", Xlsx.Hours),
        new("Booked h", Xlsx.Hours),
        new("Free h", Xlsx.Hours),
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

    public static byte[] Workbook(
        TimeZoneInfo tz,
        DateTimeOffset now,
        long snapshotId,
        DateTimeOffset? generatedAt,
        OrderedDictionary<string, object?> filters,
        List<CalendarEntry> entries,
        List<TechDay> days,
        List<Block> demand,
        List<Block> schedule
    )
    {
        using var wb = new XLWorkbook();
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
