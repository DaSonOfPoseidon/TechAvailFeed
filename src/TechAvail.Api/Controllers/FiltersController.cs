using Microsoft.AspNetCore.Mvc;
using TechAvail.Core;
using TechAvail.Data;

namespace TechAvail.Api.Controllers;

public sealed class FiltersController(IFeedReads store, ApiSettings settings, TimeProvider clock) : V1Controller(store, settings, clock)
{
    public sealed record Tech(string TechId, string TechName, string Region, string Calendar);

    public sealed record FiltersResponse(SnapshotInfo? Snapshot, List<string> Regions, List<string> Skills, List<Tech> Techs);

    // Regions, skills and technicians for filter dropdowns.
    [HttpGet("filters")]
    public FiltersResponse Get()
    {
        var (blocks, snapshot) = Latest();
        var techs = new OrderedDictionary<string, (string Name, string Region)>();
        var kinds = new Dictionary<string, HashSet<string>>();
        var skills = new HashSet<string>();
        var regions = new HashSet<string>();
        // Install shifts first, so a tech with both lists their install region.
        foreach (var b in blocks.OrderBy(b => b.Kind != "shift"))
        {
            if (Availability.Calendars.Values.Contains(b.Kind))
            {
                techs.TryAdd(b.TechId, (b.TechName, b.Region));
                if (!kinds.TryGetValue(b.TechId, out var set))
                    kinds[b.TechId] = set = [];
                set.Add(b.Kind);
                skills.UnionWith(b.Skills.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0));
            }
            if (b.Region.Length > 0)
                regions.Add(b.Region);
        }
        return new FiltersResponse(
            Info(snapshot),
            [.. regions.Order(StringComparer.Ordinal)],
            [.. skills.Order(StringComparer.Ordinal)],
            [
                .. techs
                    .Select(t => new Tech(t.Key, t.Value.Name, t.Value.Region, Diagnostics.CalendarOf(kinds[t.Key])))
                    .OrderBy(t => t.TechName, StringComparer.Ordinal)
                    .ThenBy(t => t.TechId, StringComparer.Ordinal),
            ]
        );
    }
}
