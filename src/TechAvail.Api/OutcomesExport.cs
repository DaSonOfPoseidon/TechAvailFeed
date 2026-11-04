using ClosedXML.Excel;
using TechAvail.Core;
using Column = TechAvail.Api.Xlsx.Column;

namespace TechAvail.Api;

public static class OutcomesExport
{
    internal static readonly (string Name, object? Text)[] Definitions =
    [
        ("Planned", "Install jobs and FIELD/TC tickets assigned for the day in the first snapshot between 06:00 and 07:00."),
        ("d0 / d1 / d2", "The last snapshot before midnight ending the plan day, the next day, and the day after."),
        ("Outcome", "The latest checkpoint's result: d2 for final days, so far for provisional."),
        (
            "Pulled d0",
            "Planned jobs canceled, unscheduled or rescheduled by the end of the plan day, "
                + "split by how far the tech got (in progress, en route) and by who owns the incomplete reason."
        ),
        (
            "Pulled customer / Pulled Socket",
            "Pulled jobs whose incomplete code names the bucket the job ended in (CX_/SX_ canceled, CR_/SR_ rescheduled, "
                + "CU_/SU_ unscheduled): C is the customer, S is Socket."
        ),
        (
            "Pulled no reason",
            "Pulled jobs with no usable code: blank, free text, a deactivated code, a code for another bucket, "
                + "or a day finalized before reasons were stored."
        ),
        ("Reason", "The job's incomplete code at d0."),
        ("Pulled reasons", "The sheet counting pulled jobs per incomplete code, for the days and filters of the export."),
        ("Provisional", "D+2 hasn't ended yet, so the day's outcomes can still change."),
    ];

    static readonly Column[] DayColumns =
    [
        new("Date", Xlsx.Date, 12),
        new("Status"),
        new("Provisional"),
        new("Kind"),
        new("Planned"),
        .. Outcomes.Checkpoints.Select(c => new Column($"Completed {c}")),
        .. Outcomes.Checkpoints.Select(c => new Column($"Completion {c}", Xlsx.Percent)),
        .. Outcomes.OutcomeNames.Select(o => new Column(Capitalize(o.Replace('_', ' ')))),
        new("Pulled d0"),
        .. Outcomes.ReachedNames.Select(r => new Column($"Pulled {r.Replace('_', ' ')}")),
        new("Pulled, prereqs open"),
        new("Pulled customer"),
        new("Pulled Socket"),
        new("Pulled no reason"),
    ];

    static readonly Column[] ItemColumns =
    [
        new("Plan date", Xlsx.Date, 12),
        new("Provisional"),
        new("Kind"),
        new("Ref"),
        new("Tech id"),
        new("Tech"),
        new("Region"),
        new("Task type"),
        new("Planned start", Xlsx.Time, 17),
        new("d0"),
        new("d1"),
        new("d2"),
        new("Reached"),
        new("Prereqs open"),
        new("Reason"),
    ];

    static readonly Column[] ReasonColumns = [new("Code"), new("Bucket"), new("Responsibility"), new("Count")];

    static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant();

    static List<object?[]> DayRows(OutcomeKpis kpis)
    {
        var rows = new List<object?[]>();
        foreach (var day in kpis.Days)
        {
            if (day.Job is not { } job || day.Ticket is not { } ticket)
            {
                var row = new object?[DayColumns.Length];
                (row[0], row[1], row[2]) = (day.Date, day.Status, day.Provisional);
                rows.Add(row);
                continue;
            }
            foreach (var (kind, stats, pulled) in new[] { ("job", (KindStats)job, job.PulledD0), ("ticket", ticket, null) })
                rows.Add(
                    [
                        day.Date,
                        day.Status,
                        day.Provisional,
                        kind,
                        stats.Planned,
                        .. Outcomes.Checkpoints.Select(c => (object?)stats.Completed[c]),
                        .. Outcomes.Checkpoints.Select(c => (object?)stats.CompletionRate[c]),
                        .. Outcomes.OutcomeNames.Select(o => (object?)stats.Outcome[o]),
                        pulled?.Total,
                        .. Outcomes.ReachedNames.Select(r => (object?)pulled?.Reached(r)),
                        pulled?.PrereqsOpen,
                        pulled?.Customer,
                        pulled?.Socket,
                        pulled?.NoReason,
                    ]
                );
        }
        return rows;
    }

    static List<object?[]> ItemRows(List<(DayOutcome Outcome, bool Provisional)> outcomes, string? region, string? tech) =>
    [
        .. outcomes
            .OrderBy(o => o.Outcome.Day)
            .SelectMany(o =>
                o.Outcome.Items.Where(i => (region is null || InstallAvailability.InRegion(i.Region, region)) && (tech is null || i.TechId == tech))
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
                                i.IncompleteReason,
                            ]
                    )
            ),
    ];

    static List<object?[]> ReasonRows(OutcomeKpis kpis) =>
    [
        .. kpis
            .Totals.Job.PulledD0.ByReason.OrderByDescending(r => r.Value)
            .ThenBy(r => r.Key, StringComparer.Ordinal)
            .Select(r =>
            {
                var reason = IncompleteReasons.Parse(r.Key)!;
                return (object?[])[r.Key, reason.Bucket, Capitalize(reason.Owner), r.Value];
            }),
    ];

    // kpis is already filtered; the items are filtered here.
    internal static void WriteSheets(
        XLWorkbook wb,
        List<(DayOutcome Outcome, bool Provisional)> outcomes,
        OutcomeKpis kpis,
        string? region,
        string? tech
    )
    {
        Xlsx.WriteSheet(wb, "Outcomes by day", DayColumns, DayRows(kpis));
        Xlsx.WriteSheet(wb, "Outcome items", ItemColumns, ItemRows(outcomes, region, tech));
        Xlsx.WriteSheet(wb, "Pulled reasons", ReasonColumns, ReasonRows(kpis));
    }

    public static byte[] Workbook(
        TimeZoneInfo tz,
        DateTimeOffset now,
        DateOnly start,
        DateOnly end,
        string? region,
        string? tech,
        List<(DayOutcome Outcome, bool Provisional)> outcomes
    )
    {
        using var wb = new XLWorkbook();
        WriteSheets(wb, outcomes, Kpis.OutcomeKpis(outcomes, region, tech), region, tech);
        new AboutSheet(wb)
            .ExportedAt(tz, now)
            .Filters(
                new OrderedDictionary<string, object?>
                {
                    ["region"] = region,
                    ["tech"] = tech,
                    ["from"] = start,
                    ["to"] = end,
                }
            )
            .Section("Metric", "Definition", Definitions);
        return Xlsx.Save(wb);
    }
}
