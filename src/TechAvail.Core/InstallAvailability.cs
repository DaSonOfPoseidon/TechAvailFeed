using System.Globalization;

namespace TechAvail.Core;

// Free and bookable booking windows for one area, on one day or summed over the grid's days.
public sealed record AvailabilityCell(int Free, int Bookable, string Status)
{
    public double? Share => Bookable > 0 ? (double)Free / Bookable : null;
}

public sealed record AvailabilityRow(string Area, List<AvailabilityCell> Days, AvailabilityCell Total);

public sealed record InstallGrid(List<DateOnly> Days, List<AvailabilityRow> Rows, List<string> Exceptions);

// The VP's "Residential Install Availability" update: same day and the next days only, so it doesn't
// promote scheduling out. Availability is the share of an area's bookable booking windows still free
// (TechDay.FreeWindows / BookableWindows); today's windows count only if they haven't started.
public static class InstallAvailability
{
    public const string Title = "Residential Install Availability";
    public const int GridDays = 7;

    // Same day and next day get the written exceptions; the grid shows the rest.
    public const int ExceptionDays = 2;

    // Below ClosedBelow is Closed (red), below LimitedBelow is Limited (yellow), else Open (green).
    public const double ClosedBelow = 0.15;
    public const double LimitedBelow = 0.30;

    public const string Open = "Open";
    public const string Limited = "Limited";
    public const string Closed = "Closed";
    public const string NoSlots = "No slots";

    // The VP's areas, as the feed's territories. A territory not listed is a row of its own.
    public static readonly (string Area, string[] Territories)[] Areas =
    [
        ("Carrollton", ["Carrollton"]),
        ("Central MO", ["Columbia Core", "Jefferson City Area", "Boonville-Fayette", "Moberly-Mexico Corridor", "Lake Ozark Area"]),
        ("Clinton-Butler", ["Clinton-Butler"]),
        ("Hannibal-Bowling Green", ["Hannibal-Bowling Green"]),
        ("Harrisonville-Pleasant Hill", ["Harrisonville-Pleasant Hill"]),
        ("Kirksville-Chillicothe", ["Kirksville-Chillicothe"]),
        ("Lamar-Webb City", ["Lamar-Webb City"]),
        ("Lebanon-Rolla", ["Lebanon-Rolla"]),
        ("Oak Grove-Odessa", ["Oak Grove-Odessa"]),
        ("STL Area", ["St Louis West", "Illinois Metro East", "Illinois South", "Troy-Wentzville"]),
        ("WKLS", ["Warrensburg-Knob Noster", "Sedalia-La Monte"]),
    ];

    static readonly Dictionary<string, string> AreaOf = Areas
        .SelectMany(a => a.Territories.Select(t => (Territory: t, a.Area)))
        .ToDictionary(p => p.Territory, p => p.Area);

    public static string AreaFor(string territory) => AreaOf.GetValueOrDefault(territory, territory);

    // An area with nothing bookable is Closed (its techs are off or not scheduled), unless no area
    // has slots that day, as on a Sunday.
    public static string Status(int free, int bookable, bool daysHaveSlots) =>
        bookable == 0 ? (daysHaveSlots ? Closed : NoSlots)
        : (double)free / bookable < ClosedBelow ? Closed
        : (double)free / bookable < LimitedBelow ? Limited
        : Open;

    // Every VP area is shown, even one with no techs in the grid's days. A region-filtered export shows
    // only that region's area (so it doesn't list the others as closed), even when it has no techs.
    public static InstallGrid Build(IEnumerable<TechDay> techDays, DateOnly start, int days = GridDays, string? region = null)
    {
        List<DateOnly> dates = [.. Enumerable.Range(0, days).Select(start.AddDays)];
        var counts = techDays
            .Where(d => d.Region.Length > 0 && start <= d.WorkDate && d.WorkDate <= dates[^1])
            .GroupBy(d => (Area: AreaFor(d.Region), d.WorkDate))
            .ToDictionary(g => g.Key, g => (Free: g.Sum(d => d.FreeWindows), Bookable: g.Sum(d => d.BookableWindows)));
        var seen = counts.Keys.Select(k => k.Area).ToHashSet();
        var known = Areas.Select(a => a.Area).ToHashSet();
        List<string> areas =
            region is not null
                ? [AreaFor(region)]
                : [.. Areas.Select(a => a.Area), .. seen.Where(a => !known.Contains(a)).Order(StringComparer.Ordinal)];
        var withSlots = dates.ToDictionary(d => d, d => counts.Any(c => c.Key.WorkDate == d && c.Value.Bookable > 0));
        var anySlots = withSlots.Values.Any(v => v);

        List<AvailabilityRow> rows =
        [
            .. areas.Select(area =>
            {
                List<AvailabilityCell> cells =
                [
                    .. dates.Select(d =>
                    {
                        var (free, bookable) = counts.GetValueOrDefault((area, d));
                        return new AvailabilityCell(free, bookable, Status(free, bookable, withSlots[d]));
                    }),
                ];
                int free = cells.Sum(c => c.Free), bookable = cells.Sum(c => c.Bookable);
                return new AvailabilityRow(area, cells, new AvailabilityCell(free, bookable, Status(free, bookable, anySlots)));
            }),
        ];
        return new InstallGrid(dates, rows, Exceptions(dates, rows));
    }

    // A grid shorter than ExceptionDays (days=1) speaks only for the days it has. A day no area can
    // book (a Sunday) is an exception for every area, not availability.
    static List<string> Exceptions(List<DateOnly> dates, List<AvailabilityRow> rows)
    {
        var when = dates.Count >= ExceptionDays ? "same day/next day" : "same day";
        var notes = dates
            .Take(ExceptionDays)
            .Select(
                (date, i) =>
                    (
                        date,
                        Lines: rows.Count > 0 && rows.All(r => r.Days[i].Status == NoSlots)
                            ? [$"All areas - {NoSlots}"]
                            : rows.Where(r => r.Days[i].Status == Closed).Select(r => $"{r.Area} - {Closed}").ToList()
                    )
            )
            .Where(d => d.Lines.Count > 0)
            .ToList();
        if (notes.Count == 0)
            return [$"All areas have {when} availability."];
        List<string> lines = [$"All areas have {when} availability with the following notes/exceptions:"];
        foreach (var (date, notesFor) in notes)
            lines.AddRange(["", date.ToString("dddd M/d", CultureInfo.InvariantCulture), .. notesFor]);
        return lines;
    }
}
