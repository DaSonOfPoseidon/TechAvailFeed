using TechAvail.Core.Parsing;

namespace TechAvail.Core;

// A blocks snapshot and when it was generated, as naive local time.
public sealed record Snapshot(long Id, DateTime At);

public sealed class Planned(string kind, string refId, string techId, string techName, DateTime plannedStart)
{
    public string Kind { get; } = kind;
    public string RefId { get; } = refId;
    public string TechId { get; } = techId;
    public string TechName { get; } = techName;
    public DateTime PlannedStart { get; } = plannedStart;

    // Checkpoint name -> outcome, in checkpoint order.
    public OrderedDictionary<string, string> Outcomes { get; init; } = [];
    public string Reached { get; set; } = "unknown";
    public bool? PrereqsOpen { get; set; }
    public string Region { get; init; } = "";
    public string TaskType { get; init; } = "";
}

public sealed record DayOutcome(
    DateOnly Day,
    string Status, // "ok" or "no_morning"
    Snapshot? Morning = null,
    List<Planned>? Items = null,
    OrderedDictionary<string, int>? AddedAfterMorning = null
)
{
    public List<Planned> Items { get; init; } = Items ?? [];
    public OrderedDictionary<string, int> AddedAfterMorning { get; init; } = AddedAfterMorning ?? [];
}

// Port of feed/outcomes.py: what happened to the work planned for each day.
public static class Outcomes
{
    // The morning plan is the first snapshot in [06:00, 07:00) local; without one, never guess.
    static readonly TimeOnly Morning = new(6, 0);
    static readonly TimeSpan MorningWindow = TimeSpan.FromHours(1);

    // The last snapshot before the midnight ending the plan day (d0), the next day (d1) and the
    // day after (d2).
    public static readonly string[] Checkpoints = ["d0", "d1", "d2"];

    const int SentinelYear = 9999;
    static readonly string[] InHouse = ["FIELD", "TC"];
    static readonly Dictionary<string, string[]> Completed = new() { ["job"] = ["C"], ["ticket"] = ["C", "R"] };
    static readonly Dictionary<string, string[]> Canceled = new() { ["job"] = ["U", "X"], ["ticket"] = ["D"] };

    // The history covers installs only; rows from before the feed carried a task_type have "".
    static readonly string[] InstallTypes = ["3", "23", "36", "120"];

    // Dead before the day started, so never part of the morning plan.
    static readonly Dictionary<string, string[]> NotPlanned = new() { ["job"] = ["U", "X"], ["ticket"] = ["D"] };

    public static readonly string[] OutcomeNames =
    [
        "completed",
        "canceled",
        "handed_off",
        "unscheduled",
        "rescheduled",
        "unassigned",
        "open",
        "missing",
    ];
    public static readonly string[] Kinds = ["job", "ticket"];

    // A job pulled from the plan day: the cases where it matters whether a tech had engaged.
    static readonly string[] Pulled = ["canceled", "unscheduled", "rescheduled"];

    // How far the tech got on the plan day, from the status trace.
    public static readonly string[] ReachedNames = ["in_progress", "en_route", "not_started", "unknown"];

    // A pre-drop/pre-bury that isn't Completed or dropped can keep the install from going in.
    static readonly string[] PrereqsDone = ["C", "U", "X"];

    // job_moved / job_unassigned are the same job off the calendar or without a tech.
    public static string BaseKind(string kind) => kind.Split('_', 2)[0];

    public static Snapshot? FindMorning(IEnumerable<Snapshot> snapshots, DateOnly day)
    {
        var start = day.ToDateTime(Morning);
        return snapshots.Where(s => start <= s.At && s.At < start + MorningWindow).MinBy(s => s.At);
    }

    // Before the boundary has passed this is simply the latest snapshot: the outcome so far.
    public static Snapshot? FindCheckpoint(IEnumerable<Snapshot> snapshots, Snapshot morning, DateOnly day, int offset)
    {
        var boundary = day.AddDays(offset + 1).ToDateTime(TimeOnly.MinValue);
        return snapshots.Where(s => morning.At <= s.At && s.At < boundary).MaxBy(s => s.At);
    }

    static Dictionary<(string, string), Block> Index(IEnumerable<Block> blocks)
    {
        var found = new Dictionary<(string, string), Block>();
        foreach (var block in blocks)
        {
            var kind = BaseKind(block.Kind);
            if (Kinds.Contains(kind) && block.RefId.Length > 0)
                found.TryAdd((kind, block.RefId), block);
        }
        return found;
    }

    // The work's region (the address geocode); otherwise the tech's shift region.
    public static OrderedDictionary<(string Kind, string RefId), Planned> PlannedItems(
        IEnumerable<Block> blocks,
        DateOnly day,
        IReadOnlyDictionary<string, string>? regions = null
    )
    {
        var planned = new OrderedDictionary<(string, string), Planned>();
        foreach (var block in blocks)
        {
            if (!Kinds.Contains(block.Kind) || block.WorkDate != day || block.RefId.Length == 0)
                continue;
            if (NotPlanned[block.Kind].Contains(block.Status))
                continue;
            if (block.Kind == "job" && block.TaskType.Length > 0 && !InstallTypes.Contains(block.TaskType))
                continue;
            planned.TryAdd(
                (block.Kind, block.RefId),
                new Planned(block.Kind, block.RefId, block.TechId, block.TechName, block.StartsAt)
                {
                    Region = block.Region.Length > 0 ? block.Region : regions?.GetValueOrDefault(block.TechId) ?? "",
                    TaskType = block.TaskType,
                }
            );
        }
        return planned;
    }

    public static string Classify(string kind, DateOnly day, Block? block)
    {
        if (block is null)
            return "missing";
        if (Completed[kind].Contains(block.Status))
            return "completed";
        if (Canceled[kind].Contains(block.Status))
            return "canceled";
        // Rows from before the feed carried a department have "", which says nothing.
        if (block.Department.Length > 0 && !InHouse.Contains(block.Department))
            return "handed_off";
        if (block.StartsAt.Year == SentinelYear)
            return "unscheduled";
        if (DateOnly.FromDateTime(block.StartsAt) != day)
            return "rescheduled";
        if (block.TechId.Length == 0)
            return "unassigned";
        return "open";
    }

    // Every task row carries modified_at once the feed query has the trace columns.
    public static string Reached(DateOnly day, Block? block)
    {
        if (block?.ModifiedAt is null)
            return "unknown";
        if (block.InprogressAt is { } inProgress && DateOnly.FromDateTime(inProgress) == day)
            return "in_progress";
        if (block.EnrouteAt is { } enRoute && DateOnly.FromDateTime(enRoute) == day)
            return "en_route";
        return "not_started";
    }

    public static bool? PrereqsOpen(Block? block)
    {
        if (block?.ModifiedAt is null)
            return null;
        var statuses = block
            .PrereqsStatus.Split(", ")
            .Where(item => item.Length > 0)
            .Select(item => item.Split(": ")[^1]);
        return statuses.Any(status => !PrereqsDone.Contains(status));
    }

    public static DayOutcome DayOutcome(
        DateOnly day,
        Snapshot? morning,
        IReadOnlyList<Block> morningBlocks,
        IReadOnlyDictionary<string, IReadOnlyList<Block>?> checkpoints,
        IReadOnlyDictionary<string, string>? regions = null
    )
    {
        if (morning is null)
            return new DayOutcome(day, "no_morning");
        var planned = PlannedItems(morningBlocks, day, regions);
        foreach (var name in Checkpoints.Where(checkpoints.ContainsKey))
        {
            if (checkpoints[name] is not { } blocks)
                continue;
            var found = Index(blocks);
            foreach (var (key, item) in planned)
                item.Outcomes[name] = Classify(item.Kind, day, found.GetValueOrDefault(key));
        }
        var added = Kinds.ToDictionary(k => k, _ => 0);
        if (checkpoints.GetValueOrDefault("d0") is { } d0)
        {
            var found = Index(d0);
            foreach (var (key, item) in planned)
            {
                item.Reached = Reached(day, found.GetValueOrDefault(key));
                item.PrereqsOpen = PrereqsOpen(found.GetValueOrDefault(key));
            }
            foreach (var key in PlannedItems(d0, day).Keys)
                if (!planned.ContainsKey(key))
                    added[key.Kind]++;
        }
        return new DayOutcome(
            day,
            "ok",
            morning,
            [
                .. planned
                    .Values.OrderBy(p => p.Kind, StringComparer.Ordinal)
                    .ThenBy(p => p.PlannedStart)
                    .ThenBy(p => p.RefId, StringComparer.Ordinal),
            ],
            new OrderedDictionary<string, int>(Kinds.Select(k => KeyValuePair.Create(k, added[k])))
        );
    }

    // Per kind: planned count, completions per checkpoint, the d2 outcome counts, additions after
    // the morning, and for jobs how far techs got on the ones pulled at d0. Keys and nesting match
    // the Python dict, which the API serves as is.
    public static OrderedDictionary<string, object> Summarise(DayOutcome outcome)
    {
        var byKind = new OrderedDictionary<string, object>();
        foreach (var kind in Kinds)
        {
            var items = outcome.Items.Where(i => i.Kind == kind).ToList();
            var latest = items.Select(i => i.Outcomes.GetValueOrDefault("d2")).ToList();
            var entry = new OrderedDictionary<string, object> { ["planned"] = items.Count };
            foreach (var name in Checkpoints)
                entry[$"completed_{name}"] = items.Count(i => i.Outcomes.GetValueOrDefault(name) == "completed");
            entry["as_of_d2"] = new OrderedDictionary<string, int>(
                OutcomeNames.Select(name => KeyValuePair.Create(name, latest.Count(l => l == name)))
            );
            entry["added_after_morning"] = outcome.AddedAfterMorning.GetValueOrDefault(kind);
            byKind[kind] = entry;
        }
        var pulled = outcome
            .Items.Where(i => i.Kind == "job" && Pulled.Contains(i.Outcomes.GetValueOrDefault("d0")))
            .ToList();
        var pulledD0 = new OrderedDictionary<string, int> { ["total"] = pulled.Count };
        foreach (var name in ReachedNames)
            pulledD0[name] = pulled.Count(i => i.Reached == name);
        pulledD0["prereqs_open"] = pulled.Count(i => i.PrereqsOpen == true);
        ((OrderedDictionary<string, object>)byKind["job"])["pulled_d0"] = pulledD0;
        return byKind;
    }
}
