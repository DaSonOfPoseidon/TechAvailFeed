using System.Globalization;

namespace TechAvail.Core;

public sealed record AvailabilityCell(int Free, int Bookable, double? Share, string Status);

public sealed record AvailabilityRow(string Area, List<AvailabilityCell> Days, AvailabilityCell Total);

public sealed record InstallGrid(List<DateOnly> Days, List<AvailabilityRow> Rows, List<string> Exceptions);

// Rules: docs/business-rules.md#install-availability-vp-update
public static class InstallAvailability
{
    public const string Title = "Residential Install Availability";

    public const int ExceptionDays = 2;

    public const double LimitedBelow = 0.30;

    public const string Open = "Open";
    public const string Limited = "Limited";
    public const string Full = "Full";

    // Every tech the area has that day is on time off (TechDay.OnTimeOff, which includes a day off with no shift).
    public const string NoTechs = "No Techs";

    // No tech is scheduled there, or it isn't a booking day (Sunday).
    public const string Closed = "Closed";

    // Any other territory is an area of its own, so a region MBS renames or adds needs no change here.
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

    // Rounded half up like Excel's "0%", in integers: in doubles 29/200 * 100 falls just under 14.5.
    public static int Percent(int free, int bookable) => bookable > 0 ? (200 * free + bookable) / (2 * bookable) : 0;

    // Classified by the percent shown, so 0.4% is Full and 29.6% is Open.
    public static string Status(int percent) =>
        percent == 0 ? Full
        : percent < (int)Math.Round(LimitedBelow * 100) ? Limited
        : Open;

    static AvailabilityCell Cell(int free, int bookable, string? status = null)
    {
        if (status is not null)
            return new(free, bookable, null, status);
        double share = bookable > 0 ? (double)free / bookable : 0;
        return new(free, bookable, share, Status(Percent(free, bookable)));
    }

    static AvailabilityCell DayCell(DateOnly date, List<TechDay> techs)
    {
        if (!Availability.BookingWindows(date).Any())
            return Cell(0, 0, Closed);
        if (techs.Any(t => t.AvailableHours > 0 || t.BookableWindows > 0))
            return Cell(techs.Sum(t => t.FreeWindows), techs.Sum(t => t.BookableWindows));
        return Cell(0, 0, techs.Any(t => t.OnTimeOff) ? NoTechs : Closed);
    }

    // dates are the grid's columns, in order (Availability.ExportDates).
    public static InstallGrid Build(IEnumerable<TechDay> techDays, IReadOnlyList<DateOnly> dates, DateOnly today, string? region = null)
    {
        var wanted = dates.ToHashSet();
        var byCell = techDays
            .Where(d => d.Region.Length > 0 && wanted.Contains(d.WorkDate))
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
                    : Cell(0, 0, cells.Any(c => c.Status == NoTechs) ? NoTechs : Closed);
                return new AvailabilityRow(area, cells, total);
            }),
        ];
        return new InstallGrid([.. dates], rows, Exceptions([.. dates], today, rows));
    }

    static readonly string[] Unavailable = [Full, NoTechs, Closed];

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
                        Lines: rows.All(r => r.Days[i].Status == Closed)
                            ? [$"All areas - {Closed}"]
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
