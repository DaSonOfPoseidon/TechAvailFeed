using System.Globalization;

namespace TechAvail.Core;

// Free and bookable booking windows for one area, on one day or summed over the grid's days. Share is
// null when no tech could take work (No Techs, Not Open); working techs with nothing bookable left are 0.
public sealed record AvailabilityCell(int Free, int Bookable, double? Share, string Status);

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

    // Nothing bookable because every tech the area has that day is on time off.
    public const string NoTechs = "No Techs";

    // Nothing bookable because no tech is scheduled there, or it isn't a booking day (Sunday).
    public const string NotOpen = "Not Open";

    // The VP's areas that group several of the feed's territories. Any other territory is an area of its
    // own, so a region MBS renames or adds needs no change here.
    public static readonly (string Area, string[] Territories)[] Areas =
    [
        ("Central MO", ["Columbia Core", "Jefferson City Area", "Boonville-Fayette", "Moberly-Mexico Corridor", "Lake Ozark Area"]),
        ("STL Area", ["St Louis West", "Illinois Metro East", "Illinois South", "Troy-Wentzville"]),
        ("WKLS", ["Warrensburg-Knob Noster", "Sedalia-La Monte"]),
    ];

    static readonly Dictionary<string, string> AreaOf = Areas
        .SelectMany(a => a.Territories.Select(t => (Territory: t, a.Area)))
        .ToDictionary(p => p.Territory, p => p.Area);

    public static string AreaFor(string territory) => AreaOf.GetValueOrDefault(territory, territory);

    // The whole percent the sheet shows, rounded half up like Excel's "0%" format. Integer arithmetic,
    // so 29/200 is exactly 14.5 and rounds to 15 (in doubles 0.145 * 100 falls just under 14.5).
    public static int Percent(int free, int bookable) => bookable > 0 ? (200 * free + bookable) / (2 * bookable) : 0;

    // A cell is classified as shown: 14.9% reads 15% and is Limited.
    public static string Status(int percent) =>
        percent < (int)Math.Round(ClosedBelow * 100) ? Closed
        : percent < (int)Math.Round(LimitedBelow * 100) ? Limited
        : Open;

    static AvailabilityCell Cell(int free, int bookable, string? status = null)
    {
        if (status is not null)
            return new(free, bookable, null, status);
        double share = bookable > 0 ? (double)free / bookable : 0;
        return new(free, bookable, share, Status(Percent(free, bookable)));
    }

    // One area's day. A tech with available time or a bookable window is working, and their share
    // counts even when nothing is left (all booked, or today's windows have started): 0%, Closed.
    static AvailabilityCell DayCell(DateOnly date, List<TechDay> techs)
    {
        if (!Availability.BookingWindows(date).Any())
            return Cell(0, 0, NotOpen);
        if (techs.Any(t => t.AvailableHours > 0 || t.BookableWindows > 0))
            return Cell(techs.Sum(t => t.FreeWindows), techs.Sum(t => t.BookableWindows));
        return Cell(0, 0, techs.Any(t => t.OnTimeOff) ? NoTechs : NotOpen);
    }

    // The areas are the ones with install techs in the grid's days, so a renamed region leaves no stale row.
    // A region-filtered export shows only that region's area (so it doesn't list the others as closed), even
    // when it has no techs.
    public static InstallGrid Build(IEnumerable<TechDay> techDays, DateOnly start, DateOnly today, int days = GridDays, string? region = null)
    {
        List<DateOnly> dates = [.. Enumerable.Range(0, days).Select(start.AddDays)];
        var byCell = techDays
            .Where(d => d.Region.Length > 0 && start <= d.WorkDate && d.WorkDate <= dates[^1])
            .GroupBy(d => (Area: AreaFor(d.Region), d.WorkDate))
            .ToDictionary(g => g.Key, g => g.ToList());
        List<string> areas = region is not null ? [AreaFor(region)] : [.. byCell.Keys.Select(k => k.Area).Distinct().Order(StringComparer.Ordinal)];

        List<AvailabilityRow> rows =
        [
            .. areas.Select(area =>
            {
                List<AvailabilityCell> cells = [.. dates.Select(d => DayCell(d, byCell.GetValueOrDefault((area, d), [])))];
                var total = cells.Any(c => c.Share is not null)
                    ? Cell(cells.Sum(c => c.Free), cells.Sum(c => c.Bookable))
                    : Cell(0, 0, cells.Any(c => c.Status == NoTechs) ? NoTechs : NotOpen);
                return new AvailabilityRow(area, cells, total);
            }),
        ];
        return new InstallGrid(dates, rows, Exceptions(dates, today, rows));
    }

    static readonly string[] Unavailable = [Closed, NoTechs, NotOpen];

    // A grid shorter than ExceptionDays (days=1) speaks only for the days it has, and one that doesn't
    // start today names its dates instead of "same day/next day". A day every area is Not Open (a
    // Sunday, or no install techs anywhere so the grid has no rows) is one exception for all areas.
    static List<string> Exceptions(List<DateOnly> dates, DateOnly today, List<AvailabilityRow> rows)
    {
        var first = dates.Take(ExceptionDays).ToList();
        var availability =
            dates[0] == today
                ? $"{(first.Count >= ExceptionDays ? "same day/next day" : "same day")} availability"
                : $"availability on {string.Join("/", first.Select(d => d.ToString("ddd M/d", CultureInfo.InvariantCulture)))}";
        var notes = first
            .Select(
                (date, i) =>
                    (
                        date,
                        Lines: rows.All(r => r.Days[i].Status == NotOpen)
                            ? [$"All areas - {NotOpen}"]
                            : rows.Where(r => Unavailable.Contains(r.Days[i].Status)).Select(r => $"{r.Area} - {r.Days[i].Status}").ToList()
                    )
            )
            .Where(d => d.Lines.Count > 0)
            .ToList();
        if (notes.Count == 0)
            return [$"All areas have {availability}."];
        List<string> lines = [$"All areas have {availability} with the following notes/exceptions:"];
        foreach (var (date, notesFor) in notes)
            lines.AddRange(["", date.ToString("dddd M/d", CultureInfo.InvariantCulture), .. notesFor]);
        return lines;
    }
}
