using System.Globalization;
using TechAvail.Core.Parsing;

namespace TechAvail.Core;

// One row of "Areas/Techs of Concern". TechName is null for an area-wide concern (an overbooked slot).
public sealed record ConcernRow(string Region, string Area, string? TechName, string Reason);

public sealed record RegionStatus(string Region, string Status, int Concerns);

public sealed record StatusReport(List<RegionStatus> Regions, List<ConcernRow> Concerns, List<JeopardyRow> Jeopardy);

// The VP's status update (sent at 10 AM, 1 PM, 3 PM and 5 PM): a Green/Yellow/Red status per region, and the
// areas and techs behind it. The feed's region is the VP's area; Regions groups the areas the way
// MBSReporter's multiregion rules do (notes/multiregion_limits.md there).
public static class StatusUpdate
{
    public const string Unmapped = "Unmapped";

    public static readonly (string Region, string[] Areas)[] Regions =
    [
        ("COMO", ["Columbia Core", "Jefferson City Area", "Boonville-Fayette", "Moberly-Mexico Corridor"]),
        ("STL West", ["St Louis West", "Troy-Wentzville", "Hannibal-Bowling Green"]),
        ("STL East", ["Illinois Metro East", "Illinois South"]),
        (
            "West",
            ["Clinton-Butler", "Harrisonville-Pleasant Hill", "Oak Grove-Odessa", "Sedalia-La Monte", "Warrensburg-Knob Noster", "Carrollton"]
        ),
        ("Southwest", ["Lamar-Webb City", "Hermitage-Humansville", "Springfield Area"]),
        ("South", ["Lebanon-Rolla"]),
        ("North", ["Kirksville-Chillicothe"]),
    ];

    static readonly Dictionary<string, string> RegionOf = Regions
        .SelectMany(r => r.Areas.Select(a => (Area: a, r.Region)))
        .ToDictionary(p => p.Area, p => p.Region);

    public static readonly string[] RegionNames = [.. Regions.Select(r => r.Region), Unmapped];

    // A region with no concern is Green, up to YellowMax is Yellow, more is Red.
    public const int YellowMax = 2;

    // The canonical booking windows (MBSReporter's multiregion rules), as starts; each is 2 hours long.
    static readonly int[] WeekdaySlots = [8, 10, 13, 15, 17];
    static readonly int[] SaturdaySlots = [9, 11, 14, 16];
    static readonly TimeSpan SlotLength = TimeSpan.FromHours(2);

    // The update for a requested time reads the first run in [at, at + Window), else the latest before.
    static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    static readonly string[] Working = ["E", "I"];
    static readonly string[] NotStarted = ["A", "O"];

    public static string RegionFor(string area) => RegionOf.GetValueOrDefault(area, Unmapped);

    public static string Status(int concerns) =>
        concerns == 0 ? "Green"
        : concerns <= YellowMax ? "Yellow"
        : "Red";

    public static Snapshot? FindAt(IEnumerable<Snapshot> snapshots, DateOnly day, TimeOnly at)
    {
        var from = day.ToDateTime(at);
        var onDay = snapshots.Where(s => DateOnly.FromDateTime(s.At) == day).ToList();
        return onDay.Where(s => from <= s.At && s.At < from + Window).MinBy(s => s.At) ?? onDay.Where(s => s.At < from).MaxBy(s => s.At);
    }

    // "8AM", "10:30AM", "1PM".
    public static string Clock(DateTime at) => at.ToString(at.Minute == 0 ? "htt" : "h:mmtt", CultureInfo.InvariantCulture);

    static string Noun(Block job) => job.Kind == "ticket" ? "trouble call" : "job";

    static string Label(Block job) => $"{Clock(job.StartsAt)} {Noun(job)}";

    static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    static string Staffed(int techs) => techs == 0 ? "no tech assigned" : $"only {Count(techs, "tech")}";

    public static StatusReport Build(IEnumerable<Block> blocks, DateOnly day, DateTime asOf)
    {
        var all = blocks.ToList();
        var jeopardy = Jeopardy.Find(all, day, asOf);
        var concerns = TechConcerns(all, day, asOf, jeopardy).Concat(Overbooked(all, day, asOf)).ToList();
        var order = RegionNames.Select((name, i) => (name, i)).ToDictionary(p => p.name, p => p.i);
        concerns =
        [
            .. concerns
                .OrderBy(c => order[c.Region])
                .ThenBy(c => c.Area, StringComparer.Ordinal)
                .ThenBy(c => c.TechName is null ? 1 : 0)
                .ThenBy(c => c.TechName, StringComparer.Ordinal),
        ];
        var counts = concerns.GroupBy(c => c.Region).ToDictionary(g => g.Key, g => g.Count());
        List<RegionStatus> regions =
        [
            .. RegionNames
                .Where(name => name != Unmapped || counts.ContainsKey(Unmapped))
                .Select(name => new RegionStatus(name, Status(counts.GetValueOrDefault(name)), counts.GetValueOrDefault(name))),
        ];
        return new StatusReport(regions, concerns, jeopardy);
    }

    // One row per tech and area: a job running past its end that holds up the tech's next one, then the
    // jobs in jeopardy that reason doesn't already cover.
    static IEnumerable<ConcernRow> TechConcerns(List<Block> blocks, DateOnly day, DateTime asOf, List<JeopardyRow> jeopardy)
    {
        var scope = Arrivals.Scope(blocks, day);
        var reasons = new Dictionary<(string Area, string TechId), (string TechName, List<string> Reasons)>();
        var covered = new HashSet<(string, string)>();

        void Add(Block job, string reason)
        {
            var key = (job.Region, job.TechId);
            if (!reasons.TryGetValue(key, out var entry))
                reasons[key] = entry = (job.TechName, []);
            entry.Reasons.Add(reason);
        }

        foreach (var jobs in scope.GroupBy(b => b.TechId))
        {
            var ordered = jobs.OrderBy(j => j.StartsAt).ToList();
            foreach (var job in ordered.Where(j => Working.Contains(j.Status) && j.EndsAt < asOf))
            {
                // A later job already started means the tech moved on and this one's status is stale.
                var later = ordered.Where(j => j.StartsAt > job.StartsAt).ToList();
                var next = later.FirstOrDefault();
                if (next is null || later.Any(j => !NotStarted.Contains(j.Status)) || next.StartsAt > asOf + Jeopardy.Lead)
                    continue;
                Add(job, $"{Label(job)} going long – {Clock(next.StartsAt)} in jeopardy");
                covered.Add((job.Kind, job.RefId));
                covered.Add((next.Kind, next.RefId));
            }
        }
        foreach (var row in jeopardy.Where(r => !covered.Contains((r.Job.Kind, r.Job.RefId))))
            Add(row.Job, $"{Label(row.Job)} in jeopardy");

        // Two 3PM trouble calls read "3PM trouble call in jeopardy ×2", not the same reason twice.
        static string Join(List<string> reasons) =>
            string.Join("; ", reasons.GroupBy(r => r).Select(g => g.Count() == 1 ? g.Key : $"{g.Key} ×{g.Count()}"));

        return reasons.Select(p => new ConcernRow(RegionFor(p.Key.Area), p.Key.Area, p.Value.TechName, Join(p.Value.Reasons)));
    }

    // An area's slot that isn't over yet holds more open jobs than techs assigned to them (an unassigned
    // job brings no tech).
    static IEnumerable<ConcernRow> Overbooked(List<Block> blocks, DateOnly day, DateTime asOf)
    {
        int[] starts = day.DayOfWeek switch
        {
            DayOfWeek.Sunday => [],
            DayOfWeek.Saturday => SaturdaySlots,
            _ => WeekdaySlots,
        };
        var open = blocks
            .Where(b =>
                b.Kind is "job" or "job_unassigned"
                && b.WorkDate == day
                && b.RefId.Length > 0
                && (b.Department.Length == 0 || b.Department == "FIELD")
                && !Outcomes.CompletedStatuses["job"].Contains(b.Status)
                && !Outcomes.CanceledStatuses["job"].Contains(b.Status)
            )
            .ToList();
        foreach (var hour in starts)
        {
            var from = day.ToDateTime(new TimeOnly(hour, 0));
            if (from + SlotLength <= asOf)
                continue;
            foreach (var area in open.Where(b => from <= b.StartsAt && b.StartsAt < from + SlotLength).GroupBy(b => b.Region))
            {
                var jobs = area.Select(b => b.RefId).Distinct().Count();
                var techs = area.Select(b => b.TechId).Where(t => t.Length > 0).Distinct().Count();
                if (jobs > techs)
                    yield return new ConcernRow(RegionFor(area.Key), area.Key, null, $"{Clock(from)} slot has {Count(jobs, "job")} but {Staffed(techs)}");
            }
        }
    }
}
