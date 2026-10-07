using TechAvail.Core.Parsing;

namespace TechAvail.Core;

public sealed record FreeSlot(
    DateOnly WorkDate,
    string TechId,
    string TechName,
    DateTime OpenFrom,
    DateTime OpenUntil,
    int OpenMinutes,
    string Region,
    string Skills
);

// One tech's day. available = shift - lunch - time off; booked is the jobs and tickets inside it;
// free is the usable slots (FreeSlots' rules, so today's are clipped to now + lead time).
// booked + free <= available: gaps shorter than MinMinutes are neither.
public sealed record TechDay(
    DateOnly WorkDate,
    string TechId,
    string TechName,
    string Region,
    string Skills,
    List<(DateTime Start, DateTime End)> Shifts,
    List<(DateTime Start, DateTime End)> TimeOff,
    List<Block> Work,
    List<FreeSlot> Free,
    double ShiftHours,
    double LunchHours,
    double TimeOffHours,
    double AvailableHours,
    double BookedHours,
    double FreeHours,
    int Jobs,
    int Tickets,
    bool OnTimeOff
);

public sealed record UnassignedDemand(DateOnly WorkDate, string Region, int Jobs, int Tickets, double Hours);

// Port of feed/availability.py: free time from the raw calendar blocks.
public static class Availability
{
    // Ported from the feed query: a slot must be at least this long, and can't start sooner than
    // this after "now". Unlike the query, a gap starting too soon is clipped, not dropped.
    public const int MinMinutes = 60;
    public const int LeadMinutes = 30;

    // The feed query's assumed lunch hour: 12:00-13:00, Saturdays 13:00-14:00.
    static readonly TimeOnly LunchStart = new(12, 0);
    static readonly TimeOnly SaturdayLunchStart = new(13, 0);
    static readonly TimeSpan LunchLength = TimeSpan.FromHours(1);

    // A schedule comes as "shift" on the install calendar and "shift_tc" on the TC (trouble call)
    // calendar; a dual tech's comes as both. Capacity is computed for one calendar at a time.
    public static readonly IReadOnlyDictionary<string, string> Calendars = new Dictionary<string, string>
    {
        ["install"] = "shift",
        ["tc"] = "shift_tc",
    };

    // A trouble call blocks a dual tech's install time, but an install doesn't block trouble calls.
    static readonly IReadOnlyDictionary<string, string[]> BusyOn = new Dictionary<string, string[]>
    {
        ["install"] = ["job", "ticket", "time_off"],
        ["tc"] = ["ticket", "time_off"],
    };

    // Statuses that don't occupy a tech's time: Unnecessary/Canceled tasks; Closed/Deleted/Hold/
    // Cleared tickets. A held task stays busy while a held ticket is free, a documented asymmetry.
    public static readonly IReadOnlyDictionary<string, string[]> NotBusy = new Dictionary<string, string[]>
    {
        ["job"] = ["U", "X"],
        ["ticket"] = ["C", "D", "H", "R"],
    };

    public static List<(DateTime Start, DateTime End)> Merge(IEnumerable<(DateTime Start, DateTime End)> intervals)
    {
        var merged = new List<(DateTime Start, DateTime End)>();
        foreach (var (start, end) in intervals.Order())
        {
            if (merged.Count > 0 && start <= merged[^1].End)
                merged[^1] = (merged[^1].Start, Max(merged[^1].End, end));
            else
                merged.Add((start, end));
        }
        return merged;
    }

    // Both inputs must be merged and sorted.
    public static List<(DateTime Start, DateTime End)> Subtract(
        List<(DateTime Start, DateTime End)> free,
        List<(DateTime Start, DateTime End)> busy
    )
    {
        var result = new List<(DateTime, DateTime)>();
        foreach (var (start, end) in free)
        {
            var cursor = start;
            foreach (var (busyStart, busyEnd) in busy)
            {
                if (busyEnd <= cursor || busyStart >= end)
                    continue;
                if (busyStart > cursor)
                    result.Add((cursor, busyStart));
                cursor = Max(cursor, busyEnd);
            }
            if (cursor < end)
                result.Add((cursor, end));
        }
        return result;
    }

    // Both inputs must be merged and sorted.
    public static List<(DateTime Start, DateTime End)> Intersect(
        List<(DateTime Start, DateTime End)> a,
        List<(DateTime Start, DateTime End)> b
    ) => Subtract(a, Subtract(a, b));

    public static (DateTime Start, DateTime End) Lunch(DateOnly workDate)
    {
        var start = workDate.ToDateTime(
            workDate.DayOfWeek == DayOfWeek.Saturday ? SaturdayLunchStart : LunchStart
        );
        return (start, start + LunchLength);
    }

    public static double Hours(IEnumerable<(DateTime Start, DateTime End)> intervals) =>
        intervals.Sum(i => (i.End - i.Start).TotalSeconds) / 3600;

    public static bool IsBusy(Block block, string calendar = "install") =>
        BusyOn[calendar].Contains(block.Kind)
        && !(NotBusy.TryGetValue(block.Kind, out var statuses) && statuses.Contains(block.Status));

    // One calendar's shift segments per tech and day, and every busy block per tech (time off can
    // span days). Both keep first-seen order, like Python's dicts.
    static (OrderedDictionary<(string, DateOnly), List<Block>>, OrderedDictionary<string, List<Block>>) Group(
        IEnumerable<Block> blocks,
        string calendar
    )
    {
        var shiftKind = Calendars[calendar];
        var shifts = new OrderedDictionary<(string, DateOnly), List<Block>>();
        var busy = new OrderedDictionary<string, List<Block>>();
        foreach (var block in blocks)
        {
            if (block.Kind == shiftKind)
                Add(shifts, (block.TechId, block.WorkDate), block);
            else if (IsBusy(block, calendar))
                Add(busy, block.TechId, block);
        }
        return (shifts, busy);
    }

    static void Add<TKey>(OrderedDictionary<TKey, List<Block>> groups, TKey key, Block block)
        where TKey : notnull
    {
        if (!groups.TryGetValue(key, out var list))
            groups.Add(key, list = []);
        list.Add(block);
    }

    // now is naive local time, like the block timestamps.
    public static List<FreeSlot> FreeSlots(
        IReadOnlyList<Block> blocks,
        DateTime now,
        int minMinutes = MinMinutes,
        int leadMinutes = LeadMinutes,
        bool withLunch = true,
        string calendar = "install"
    )
    {
        var (shifts, busy) = Group(blocks, calendar);
        // Rounded up to the whole minute, so clipped slots don't start at 17:00:51.
        var earliest = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute).AddMinutes(leadMinutes);
        if (now.Ticks % TimeSpan.TicksPerMinute != 0)
            earliest = earliest.AddMinutes(1);
        var minimum = TimeSpan.FromMinutes(minMinutes);
        var slots = new List<FreeSlot>();
        foreach (var ((techId, workDate), segments) in shifts)
        {
            var working = Merge(segments.Select(s => (s.StartsAt, s.EndsAt)));
            var taken = busy.GetValueOrDefault(techId, []).Select(b => (b.StartsAt, b.EndsAt)).ToList();
            if (withLunch)
                taken.Add(Lunch(workDate));
            foreach (var (gapStart, end) in Subtract(working, Merge(taken)))
            {
                var start = Max(gapStart, earliest);
                if (end - start < minimum)
                    continue;
                var first = segments[0];
                slots.Add(
                    new FreeSlot(
                        workDate,
                        techId,
                        first.TechName,
                        start,
                        end,
                        (int)Math.Round((end - start).TotalSeconds / 60, MidpointRounding.ToEven),
                        first.Region,
                        first.Skills
                    )
                );
            }
        }
        return
        [
            .. slots
                .OrderBy(s => s.WorkDate)
                .ThenBy(s => s.TechName, StringComparer.Ordinal)
                .ThenBy(s => s.OpenFrom),
        ];
    }

    static (DateTime, DateTime) DaySpan(DateOnly workDate)
    {
        var start = workDate.ToDateTime(TimeOnly.MinValue);
        return (start, start.AddDays(1));
    }

    // Every tech and date in [start, end] with a shift on this calendar, or time off. The range
    // defaults to the shift dates. A day off counts on every calendar the tech has shifts on; a
    // tech with no shifts at all stays on install, as before TC shifts were sent.
    public static List<TechDay> TechDays(
        IReadOnlyList<Block> blocks,
        DateTime now,
        DateOnly? start = null,
        DateOnly? end = null,
        string calendar = "install"
    )
    {
        var (shifts, busy) = Group(blocks, calendar);
        var scheduled = Calendars.ToDictionary(
            c => c.Key,
            c => blocks.Where(b => b.Kind == c.Value).Select(b => b.TechId).ToHashSet()
        );
        var onAny = scheduled.Values.SelectMany(s => s).ToHashSet();
        bool Belongs(string techId) =>
            scheduled[calendar].Contains(techId) || (calendar == "install" && !onAny.Contains(techId));

        var shiftDates = shifts.Keys.Select(k => k.Item2).ToList();
        var from = start ?? (shiftDates.Count > 0 ? shiftDates.Min() : null);
        var to = end ?? (shiftDates.Count > 0 ? shiftDates.Max() : null);
        if (from is not { } first || to is not { } last)
            return [];
        var free = new Dictionary<(string, DateOnly), List<FreeSlot>>();
        foreach (var slot in FreeSlots(blocks, now, calendar: calendar))
        {
            if (!free.TryGetValue((slot.TechId, slot.WorkDate), out var list))
                free[(slot.TechId, slot.WorkDate)] = list = [];
            list.Add(slot);
        }

        var keys = shifts.Keys.Where(k => first <= k.Item2 && k.Item2 <= last).ToHashSet();
        var names = new Dictionary<string, string>();
        foreach (var (techId, techBlocks) in busy)
        {
            foreach (var block in techBlocks)
            {
                names.TryAdd(techId, block.TechName);
                if (block.Kind != "time_off" || !Belongs(techId))
                    continue;
                var day = Max(DateOnly.FromDateTime(block.StartsAt), first);
                var stop = Min(DateOnly.FromDateTime(block.EndsAt), last);
                for (; day <= stop; day = day.AddDays(1))
                    keys.Add((techId, day));
            }
        }
        // A day off still belongs to the region the tech usually works in.
        var home = new Dictionary<string, Block>();
        foreach (var key in shifts.Keys.OrderBy(k => k.Item1, StringComparer.Ordinal).ThenBy(k => k.Item2))
            home.TryAdd(key.Item1, shifts[key][0]);

        var days = new List<TechDay>();
        foreach (var (techId, workDate) in keys)
        {
            var segments = shifts.GetValueOrDefault((techId, workDate), []);
            var working = Merge(segments.Select(s => (s.StartsAt, s.EndsAt)));
            var techBlocks = busy.GetValueOrDefault(techId, []);
            List<(DateTime, DateTime)> span = [DaySpan(workDate)];
            var leave = Merge(techBlocks.Where(b => b.Kind == "time_off").Select(b => (b.StartsAt, b.EndsAt)));
            var lunchTaken = Intersect(working, [Lunch(workDate)]);
            var off = Intersect(working, leave);
            var available = Subtract(working, Merge(lunchTaken.Concat(off)));
            var work = techBlocks
                .Where(b => b.Kind != "time_off" && Intersect(span, [(b.StartsAt, b.EndsAt)]).Count > 0)
                .ToList();
            var booked = Intersect(available, Merge(work.Select(b => (b.StartsAt, b.EndsAt))));
            var slots = free.GetValueOrDefault((techId, workDate), []);
            var info = segments.Count > 0 ? segments[0] : home.GetValueOrDefault(techId);
            days.Add(
                new TechDay(
                    workDate,
                    techId,
                    info?.TechName ?? names.GetValueOrDefault(techId, techId),
                    info?.Region ?? "",
                    info?.Skills ?? "",
                    working,
                    Intersect(span, leave),
                    [
                        .. work.OrderBy(b => b.StartsAt)
                            .ThenBy(b => b.Kind, StringComparer.Ordinal)
                            .ThenBy(b => b.RefId, StringComparer.Ordinal),
                    ],
                    slots,
                    Math.Round(Hours(working), 2),
                    Math.Round(Hours(lunchTaken), 2),
                    Math.Round(Hours(off), 2),
                    Math.Round(Hours(available), 2),
                    Math.Round(Hours(booked), 2),
                    Math.Round((double)slots.Sum(s => s.OpenMinutes) / 60, 2),
                    work.Where(b => b.Kind == "job").Select(b => b.RefId).Distinct().Count(),
                    work.Where(b => b.Kind == "ticket").Select(b => b.RefId).Distinct().Count(),
                    off.Count > 0 || (working.Count == 0 && Intersect(span, leave).Count > 0)
                )
            );
        }
        return
        [
            .. days.OrderBy(d => d.WorkDate)
                .ThenBy(d => d.TechName, StringComparer.Ordinal)
                .ThenBy(d => d.TechId, StringComparer.Ordinal),
        ];
    }

    // Scheduled work with no tech yet, per day and the work's region. It will take a rostered
    // tech's time, so a dashboard nets it against free capacity.
    public static List<UnassignedDemand> Unassigned(IEnumerable<Block> blocks, DateOnly today)
    {
        var totals = new Dictionary<(DateOnly, string), (int Jobs, int Tickets, double Hours)>();
        foreach (var block in blocks)
        {
            if (!block.Kind.EndsWith("_unassigned", StringComparison.Ordinal) || block.WorkDate < today)
                continue;
            var kind = block.Kind[..^"_unassigned".Length];
            if (NotBusy.TryGetValue(kind, out var statuses) && statuses.Contains(block.Status))
                continue;
            var entry = totals.GetValueOrDefault((block.WorkDate, block.Region));
            if (kind == "job")
                entry.Jobs++;
            else
                entry.Tickets++;
            entry.Hours += (block.EndsAt - block.StartsAt).TotalSeconds / 3600;
            totals[(block.WorkDate, block.Region)] = entry;
        }
        return
        [
            .. totals
                .OrderBy(t => t.Key.Item1)
                .ThenBy(t => t.Key.Item2, StringComparer.Ordinal)
                .Select(t => new UnassignedDemand(t.Key.Item1, t.Key.Item2, t.Value.Jobs, t.Value.Tickets, Math.Round(t.Value.Hours, 2))),
        ];
    }

    static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;

    static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
}
