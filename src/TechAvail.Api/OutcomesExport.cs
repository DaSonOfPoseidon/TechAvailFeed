using ClosedXML.Excel;
using TechAvail.Core;
using Column = TechAvail.Api.Xlsx.Column;

namespace TechAvail.Api;

// The outcome history as a workbook: what happened to each day's morning plan, per day and per job.
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
                + "split by how far the tech got (in progress, en route)."
        ),
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
    ];

    // str.capitalize(): first character upper, the rest lower.
    static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant();

    // One row per day and kind; a day without a usable plan gets one row with just its status.
    static List<object?[]> DayRows(OrderedDictionary<string, object?> kpis)
    {
        var rows = new List<object?[]>();
        foreach (var entry in (List<OrderedDictionary<string, object?>>)kpis["days"]!)
        {
            if ((string)entry["status"]! != "ok")
            {
                var row = new object?[DayColumns.Length];
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

    static List<object?[]> ItemRows(List<(DayOutcome Outcome, bool Provisional)> outcomes, string? region, string? tech) =>
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
    ];

    // Outcomes by day (from kpis, already filtered) and Outcome items (filtered here).
    internal static void WriteSheets(
        XLWorkbook wb,
        List<(DayOutcome Outcome, bool Provisional)> outcomes,
        OrderedDictionary<string, object?> kpis,
        string? region,
        string? tech
    )
    {
        Xlsx.WriteSheet(wb, "Outcomes by day", DayColumns, DayRows(kpis));
        Xlsx.WriteSheet(wb, "Outcome items", ItemColumns, ItemRows(outcomes, region, tech));
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
