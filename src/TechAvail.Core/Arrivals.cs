using TechAvail.Core.Parsing;

namespace TechAvail.Core;

// State is "not_started", "en_route", "arrived", or "" for anything else.
public sealed record ArrivalRow(
    string TechId,
    string TechName,
    string Kind,
    string RefId,
    string Status,
    string TaskType,
    string Region,
    DateTime ScheduledStart,
    DateTime? EnrouteAt,
    DateTime? InprogressAt,
    int? MinutesLate,
    int? MinutesEnRoute,
    string State
);

// Rules: docs/business-rules.md#arrivals-q851
public static class Arrivals
{
    static readonly TimeOnly EightFifteen = new(8, 15);
    static readonly TimeOnly Nine = new(9, 0);
    static readonly TimeOnly Eight = new(8, 0);

    static readonly string[] Departments = ["FIELD", "TC"];

    // Q851's population: work that was or is being worked on the day.
    static readonly Dictionary<string, string[]> Worked = new() { ["job"] = ["A", "C", "E", "I", "N"], ["ticket"] = ["O", "C", "E", "I", "N", "R"] };

    static readonly string[] NotStarted = ["A", "O"];
    static readonly string[] Reached = ["I", "C", "N", "R"];

    public static readonly Dictionary<string, Dictionary<string, string>> StatusNames = new()
    {
        ["job"] = new() { ["A"] = "Active", ["E"] = "En Route", ["I"] = "In Progress", ["N"] = "Incomplete", ["C"] = "Complete" },
        ["ticket"] = new()
        {
            ["O"] = "Open",
            ["E"] = "En Route",
            ["I"] = "In Progress",
            ["N"] = "Incomplete",
            ["C"] = "Closed",
            ["R"] = "Cleared",
        },
    };

    public static readonly Dictionary<string, string> KindNames = new() { ["job"] = "Task", ["ticket"] = "Trouble Call" };

    public static Snapshot? FindEightFifteen(IEnumerable<Snapshot> snapshots, DateOnly day)
    {
        var (from, until) = (day.ToDateTime(EightFifteen), day.ToDateTime(Nine));
        return snapshots.Where(s => from <= s.At && s.At < until).MinBy(s => s.At);
    }

    public static Snapshot? FindLastOn(IEnumerable<Snapshot> snapshots, DateOnly day) =>
        snapshots.Where(s => DateOnly.FromDateTime(s.At) == day).MaxBy(s => s.At);

    // Rows from before the feed carried a department have "", which says nothing.
    static bool InScope(Block b, DateOnly day) =>
        Worked.TryGetValue(b.Kind, out var statuses)
        && statuses.Contains(b.Status)
        && b.WorkDate == day
        && b.TechId.Length > 0
        && b.RefId.Length > 0
        && (b.Department.Length == 0 || Departments.Contains(b.Department));

    internal static List<Block> Scope(IEnumerable<Block> blocks, DateOnly day) =>
        [.. blocks.Where(b => InScope(b, day)).DistinctBy(b => (b.Kind, b.RefId, b.TechId))];

    // The feed sends the latest mark from the last two days.
    static DateTime? Mark(DateTime? at, DateOnly day, DateTime asOf) => at is { } t && DateOnly.FromDateTime(t) == day && t <= asOf ? t : null;

    static int? Minutes(DateTime? to, DateTime? from) => to is { } t && from is { } f ? (int)Math.Floor((t - f).TotalMinutes) : null;

    static string State(string status, DateTime? arrived, DateTime? enRoute) =>
        arrived is not null || Reached.Contains(status) ? "arrived"
        : status == "E" || enRoute is not null ? "en_route"
        : NotStarted.Contains(status) ? "not_started"
        : "";

    static ArrivalRow Row(Block job, DateTime? enRoute, DateTime? arrived, DateTime asOf, bool live) =>
        new(
            job.TechId,
            job.TechName,
            job.Kind,
            job.RefId,
            job.Status,
            job.TaskType,
            job.Region,
            job.StartsAt,
            enRoute,
            arrived,
            Minutes(arrived, job.StartsAt),
            // Still on the way: how long they had been driving when the snapshot was read.
            Minutes(arrived ?? (live ? asOf : null), enRoute),
            State(job.Status, arrived, enRoute)
        );

    static ArrivalRow Own(Block job, DateOnly day, DateTime asOf, bool live)
    {
        var arrived = Mark(job.InprogressAt, day, asOf);
        return Row(job, Mark(job.EnrouteAt, day, asOf), arrived, asOf, live);
    }

    // Q851's ladder. A missing arrival sorts last among those in progress: null is the smallest DateTime?.
    static int Tier(string status) =>
        status switch
        {
            "A" or "O" => 1,
            "E" => 2,
            "I" => 3,
            "N" => 4,
            _ => 5,
        };

    static List<ArrivalRow> Ladder(IEnumerable<ArrivalRow> rows) =>
    [
        .. rows.OrderBy(r => Tier(r.Status))
            .ThenByDescending(r => Tier(r.Status) == 3 ? r.InprogressAt : null)
            .ThenBy(r => r.ScheduledStart)
            .ThenBy(r => r.Kind, StringComparer.Ordinal)
            .ThenBy(r => r.TechName, StringComparer.Ordinal)
            .ThenBy(r => r.RefId, StringComparer.Ordinal),
    ];

    public static List<ArrivalRow> EightAm(IEnumerable<Block> blocks, DateOnly day, DateTime asOf)
    {
        var rows = new List<ArrivalRow>();
        foreach (var jobs in Scope(blocks, day).GroupBy(b => b.TechId))
        {
            var first = jobs.Where(j => TimeOnly.FromDateTime(j.StartsAt) == Eight)
                .OrderBy(j => j.Kind, StringComparer.Ordinal)
                .ThenBy(j => j.RefId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (first is null)
                continue;
            var arrivedOn = jobs.Where(j => Mark(j.InprogressAt, day, asOf) is not null).MinBy(j => j.InprogressAt);
            var enRoute =
                arrivedOn is not null ? Mark(arrivedOn.EnrouteAt, day, asOf)
                : Mark(first.EnrouteAt, day, asOf) ?? jobs.Where(j => j.Status == "E").Select(j => Mark(j.EnrouteAt, day, asOf)).Max();
            rows.Add(Row(first, enRoute, arrivedOn?.InprogressAt, asOf, live: true));
        }
        return Ladder(rows);
    }

    public static List<ArrivalRow> SoFar(IEnumerable<Block> blocks, DateOnly day, DateTime asOf) =>
        Ladder(
            Scope(blocks, day)
                .Where(b => Outcomes.CompletedStatuses[b.Kind].Contains(b.Status) || b.StartsAt <= asOf)
                .Select(b => Own(b, day, asOf, live: true))
        );

    public static List<ArrivalRow> Completed(IEnumerable<Block> blocks, DateOnly day, DateTime asOf) =>
    [
        .. Scope(blocks, day)
            .Where(b => Outcomes.CompletedStatuses[b.Kind].Contains(b.Status))
            .Select(b => Own(b, day, asOf, live: false))
            .OrderBy(r => r.TechName, StringComparer.Ordinal)
            .ThenBy(r => r.TechId, StringComparer.Ordinal)
            .ThenBy(r => r.InprogressAt ?? r.ScheduledStart)
            .ThenBy(r => r.RefId, StringComparer.Ordinal),
    ];
}
