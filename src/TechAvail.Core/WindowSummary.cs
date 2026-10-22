using TechAvail.Core.Parsing;

namespace TechAvail.Core;

// One booking window in one area: the techs who could still take a job in it, the unassigned work
// that will need one of them, and whether a 1-hour MDU job still fits.
public sealed record WindowRow(DateOnly Date, string Area, DateTime Window, int Available, int Unassigned, bool Mdu)
{
    public int Net => Available - Unassigned;
}

// MBSReporter's Q778 (region_availability_summary.sql) on the feed's tech days. A window gets a row
// when a tech is scheduled in it, has free time in it or unassigned work falls in it; one with no tech
// left over once the unassigned work is placed reads FULL.
public static class WindowSummary
{
    static readonly TimeSpan MinOverlap = TimeSpan.FromMinutes(Availability.MinMinutes);
    static readonly TimeSpan MduJob = TimeSpan.FromHours(1);
    static readonly string[] MduSkills = ["MDU"];

    static TimeSpan Overlap(DateTime start, DateTime end, DateTime from, DateTime until) =>
        (end < until ? end : until) - (start > from ? start : from);

    // The windows that still have an hour left after the lead time. Trouble calls run every day, so the
    // TC calendar's Sunday uses the weekday windows.
    static IEnumerable<DateTime> Windows(DateOnly date, string calendar, DateTime earliest) =>
        (
            calendar == "tc" && date.DayOfWeek == DayOfWeek.Sunday
                ? Availability.BookingWindows(date.AddDays(1)).Select(w => w.AddDays(-1))
                : Availability.BookingWindows(date)
        ).Where(w => w + Availability.WindowLength - (w > earliest ? w : earliest) >= MinOverlap);

    // Pass days and demand already filtered. A tech is scheduled in a window when their shift covers an
    // hour of it that hasn't started (by the lead time), and available when a free slot covers an hour of
    // it. Unassigned work counts once (the feed can repeat a row), in the first window it overlaps. An
    // MDU-skilled tech has an MDU opening when a free hour starts in the window.
    public static List<WindowRow> Build(
        IEnumerable<TechDay> days,
        IEnumerable<Block> demand,
        IReadOnlyList<DateOnly> dates,
        DateTime now,
        string calendar = "install"
    )
    {
        var earliest = Availability.Earliest(now);
        var wanted = dates.ToHashSet();
        var scheduled = new HashSet<(DateOnly, string, DateTime)>();
        var available = new Dictionary<(DateOnly, string, DateTime), HashSet<string>>();
        var mdu = new HashSet<(DateOnly, string, DateTime)>();
        var unassigned = new Dictionary<(DateOnly, string, DateTime), int>();

        foreach (var day in days.Where(d => d.Region.Length > 0 && wanted.Contains(d.WorkDate)))
        {
            var area = InstallAvailability.AreaFor(day.Region);
            var hourJobs = Availability.HasAnySkill(day.Skills, MduSkills);
            foreach (var window in Windows(day.WorkDate, calendar, earliest))
            {
                var key = (day.WorkDate, area, window);
                var end = window + Availability.WindowLength;
                var from = window > earliest ? window : earliest;
                if (day.Shifts.Any(s => Overlap(s.Start, s.End, from, end) >= MinOverlap))
                    scheduled.Add(key);
                if (day.Free.Any(f => Overlap(f.OpenFrom, f.OpenUntil, window, end) >= MinOverlap))
                {
                    if (!available.TryGetValue(key, out var techs))
                        available[key] = techs = [];
                    techs.Add(day.TechId);
                }
                if (
                    hourJobs
                    && day.Free.Any(f => (f.OpenFrom > window ? f.OpenFrom : window) is var start && start < end && start + MduJob <= f.OpenUntil)
                )
                    mdu.Add(key);
            }
        }

        var work = demand
            .Where(b => b.Region.Length > 0 && wanted.Contains(b.WorkDate))
            .DistinctBy(b => (b.Kind, b.RefId, b.StartsAt, b.EndsAt));
        foreach (var block in work)
        {
            var window = Windows(block.WorkDate, calendar, earliest)
                .Where(w => w < block.EndsAt && w + Availability.WindowLength > block.StartsAt)
                .Select(w => (DateTime?)w)
                .FirstOrDefault();
            if (window is { } w)
            {
                var key = (block.WorkDate, InstallAvailability.AreaFor(block.Region), w);
                unassigned[key] = unassigned.GetValueOrDefault(key) + 1;
            }
        }

        return
        [
            .. scheduled
                .Union(available.Keys)
                .Union(unassigned.Keys)
                .OrderBy(k => k.Item1)
                .ThenBy(k => k.Item2, StringComparer.Ordinal)
                .ThenBy(k => k.Item3)
                .Select(k => new WindowRow(
                    k.Item1,
                    k.Item2,
                    k.Item3,
                    available.GetValueOrDefault(k)?.Count ?? 0,
                    unassigned.GetValueOrDefault(k),
                    mdu.Contains(k)
                )),
        ];
    }
}
