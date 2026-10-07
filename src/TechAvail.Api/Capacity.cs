using System.Text.Json.Serialization;
using TechAvail.Core;
using TechAvail.Core.Parsing;

namespace TechAvail.Api;

// Rollups of Availability's per-tech days for the calendar and the capacity KPIs. No scheduling
// rules live here: hours come from TechDays, demand from the unassigned rows.
public class Capacity
{
    public int TechsOn { get; set; }
    public int TechsOff { get; set; }
    public double ShiftH { get; set; }
    public double AvailableH { get; set; }
    public double BookedH { get; set; }
    public double FreeH { get; set; }
    public int Jobs { get; set; }
    public int Tickets { get; set; }
    public int UnassignedJobs { get; set; }
    public int UnassignedTickets { get; set; }
    public double UnassignedH { get; set; }

    // Set by Finish(): booked / available, and the free hours left once unassigned work is placed.
    public double? Utilization { get; set; }
    public double NetH { get; set; }

    public void AddDay(TechDay day)
    {
        TechsOn += day.AvailableHours > 0 ? 1 : 0;
        TechsOff += day.OnTimeOff ? 1 : 0;
        ShiftH += day.ShiftHours;
        AvailableH += day.AvailableHours;
        BookedH += day.BookedHours;
        FreeH += day.FreeHours;
        Jobs += day.Jobs;
        Tickets += day.Tickets;
    }

    public void AddDemand(Block block)
    {
        if (block.Kind == "job_unassigned")
            UnassignedJobs++;
        else
            UnassignedTickets++;
        UnassignedH += (block.EndsAt - block.StartsAt).TotalSeconds / 3600;
    }

    public Capacity Finish()
    {
        ShiftH = Math.Round(ShiftH, 2);
        AvailableH = Math.Round(AvailableH, 2);
        BookedH = Math.Round(BookedH, 2);
        FreeH = Math.Round(FreeH, 2);
        UnassignedH = Math.Round(UnassignedH, 2);
        Utilization = AvailableH != 0 ? Math.Round(BookedH / AvailableH, 3) : null;
        NetH = Math.Round(FreeH - UnassignedH, 2);
        return this;
    }

    protected void CopyTo(Capacity other)
    {
        other.TechsOn = TechsOn;
        other.TechsOff = TechsOff;
        other.ShiftH = ShiftH;
        other.AvailableH = AvailableH;
        other.BookedH = BookedH;
        other.FreeH = FreeH;
        other.Jobs = Jobs;
        other.Tickets = Tickets;
        other.UnassignedJobs = UnassignedJobs;
        other.UnassignedTickets = UnassignedTickets;
        other.UnassignedH = UnassignedH;
        other.Utilization = Utilization;
        other.NetH = NetH;
    }

    public DatedCapacity ForDate(DateOnly date)
    {
        var row = new DatedCapacity { Date = date };
        CopyTo(row);
        return row;
    }

    public RegionCapacity ForRegion(string region)
    {
        var row = new RegionCapacity { Region = region };
        CopyTo(row);
        return row;
    }
}

public sealed class RegionCapacity : Capacity
{
    [JsonPropertyOrder(-1)]
    public string Region { get; init; } = "";
}

public sealed class DatedCapacity : Capacity
{
    [JsonPropertyOrder(-1)]
    public DateOnly Date { get; init; }
}

public sealed record CalendarEntry(DateOnly Date, Capacity Totals, List<RegionCapacity> ByRegion);

// A region's sums over a whole range. Head counts don't add up across days: summed, they are
// tech-days, so techs_on is left out and techs_off becomes tech_days_off.
public sealed record RegionTotal(
    string Region,
    double ShiftH,
    double AvailableH,
    double BookedH,
    double FreeH,
    int Jobs,
    int Tickets,
    int UnassignedJobs,
    int UnassignedTickets,
    double UnassignedH,
    double? Utilization,
    double NetH,
    int TechDaysOff
);

public static class CapacityRollup
{
    public static bool HasSkill(string skills, string skill) =>
        skills.Split(',').Select(s => s.Trim().ToUpperInvariant()).Contains(skill.ToUpperInvariant());

    public static List<TechDay> FilterDays(IEnumerable<TechDay> days, string? region, string? skill) =>
        [.. days.Where(d => (region is null || d.Region == region) && (skill is null || HasSkill(d.Skills, skill)))];

    // Live jobs and tickets with no tech yet, by the region their address maps to. They carry no
    // skill, so a skill filter doesn't narrow them. TC-department work is the TC calendar's
    // demand; everything else is install's.
    public static List<Block> UnassignedWork(
        IEnumerable<Block> blocks,
        DateOnly start,
        DateOnly end,
        string? region = null,
        string calendar = "install"
    )
    {
        var found = new List<Block>();
        foreach (var block in blocks)
        {
            if (!block.Kind.EndsWith("_unassigned", StringComparison.Ordinal) || block.WorkDate < start || block.WorkDate > end)
                continue;
            var kind = block.Kind[..^"_unassigned".Length];
            if ((block.Department == "TC") != (calendar == "tc"))
                continue;
            if (Availability.NotBusy.TryGetValue(kind, out var statuses) && statuses.Contains(block.Status))
                continue;
            if (region is not null && block.Region != region)
                continue;
            found.Add(block);
        }
        return
        [
            .. found
                .OrderBy(b => b.WorkDate)
                .ThenBy(b => b.Region, StringComparer.Ordinal)
                .ThenBy(b => b.StartsAt)
                .ThenBy(b => b.RefId, StringComparer.Ordinal),
        ];
    }

    // One entry per date in [start, end], totals plus a per-region breakdown. Pass days and
    // demand already filtered.
    public static List<CalendarEntry> Entries(IEnumerable<TechDay> days, IEnumerable<Block> demand, DateOnly start, DateOnly end)
    {
        var totals = new Dictionary<DateOnly, Capacity>();
        var regions = new Dictionary<DateOnly, Dictionary<string, Capacity>>();
        Capacity Total(DateOnly date) => totals.TryGetValue(date, out var c) ? c : totals[date] = new Capacity();
        Capacity Region(DateOnly date, string region)
        {
            if (!regions.TryGetValue(date, out var byRegion))
                regions[date] = byRegion = [];
            return byRegion.TryGetValue(region, out var c) ? c : byRegion[region] = new Capacity();
        }

        foreach (var day in days)
        {
            Total(day.WorkDate).AddDay(day);
            Region(day.WorkDate, day.Region).AddDay(day);
        }
        foreach (var block in demand)
        {
            Total(block.WorkDate).AddDemand(block);
            Region(block.WorkDate, block.Region).AddDemand(block);
        }
        var result = new List<CalendarEntry>();
        for (var current = start; current <= end; current = current.AddDays(1))
        {
            var byRegion = regions.GetValueOrDefault(current, []);
            result.Add(
                new CalendarEntry(
                    current,
                    Total(current).Finish(),
                    [.. byRegion.Keys.Order(StringComparer.Ordinal).Select(name => byRegion[name].Finish().ForRegion(name))]
                )
            );
        }
        return result;
    }

    // Sums a calendar's per-region rows over the whole range.
    public static List<RegionTotal> RegionTotals(IEnumerable<CalendarEntry> entries)
    {
        var totals = new Dictionary<string, Capacity>();
        foreach (var row in entries.SelectMany(e => e.ByRegion))
        {
            if (!totals.TryGetValue(row.Region, out var total))
                totals[row.Region] = total = new Capacity();
            total.TechsOff += row.TechsOff;
            total.ShiftH += row.ShiftH;
            total.AvailableH += row.AvailableH;
            total.BookedH += row.BookedH;
            total.FreeH += row.FreeH;
            total.Jobs += row.Jobs;
            total.Tickets += row.Tickets;
            total.UnassignedJobs += row.UnassignedJobs;
            total.UnassignedTickets += row.UnassignedTickets;
            total.UnassignedH += row.UnassignedH;
        }
        return
        [
            .. totals.Keys.Order(StringComparer.Ordinal).Select(name =>
            {
                var t = totals[name].Finish();
                return new RegionTotal(
                    name,
                    t.ShiftH,
                    t.AvailableH,
                    t.BookedH,
                    t.FreeH,
                    t.Jobs,
                    t.Tickets,
                    t.UnassignedJobs,
                    t.UnassignedTickets,
                    t.UnassignedH,
                    t.Utilization,
                    t.NetH,
                    t.TechsOff
                );
            }),
        ];
    }
}
