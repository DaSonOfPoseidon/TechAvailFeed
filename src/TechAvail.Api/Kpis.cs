using TechAvail.Core;

namespace TechAvail.Api;

// Port of api/kpis.py: rates over the outcome history's planned items. Each rate's denominator
// is the planned count of that kind; a day without a morning plan is listed but adds nothing.
// Built as ordered dictionaries with Python's keys, so the JSON matches the Python API.
public static class Kpis
{
    static readonly string[] Pulled = ["canceled", "unscheduled", "rescheduled"];

    // The latest checkpoint the item has: d2 once a day is final, the outcome so far before that.
    static string? Final(Planned item)
    {
        foreach (var name in Outcomes.Checkpoints.Reverse())
            if (item.Outcomes.TryGetValue(name, out var outcome))
                return outcome;
        return null;
    }

    static double? Rate(int count, int total) => total != 0 ? PyMath.Round((double)count / total, 3) : null;

    static OrderedDictionary<string, object?> Stats(IEnumerable<Planned> all, string kind)
    {
        var items = all.Where(i => i.Kind == kind).ToList();
        var planned = items.Count;
        var completed = new OrderedDictionary<string, int>(
            Outcomes.Checkpoints.Select(name =>
                KeyValuePair.Create(name, items.Count(i => i.Outcomes.GetValueOrDefault(name) == "completed"))
            )
        );
        var latest = items.Select(Final).ToList();
        var outcome = new OrderedDictionary<string, int>(
            Outcomes.OutcomeNames.Select(name => KeyValuePair.Create(name, latest.Count(l => l == name)))
        );
        var result = new OrderedDictionary<string, object?>
        {
            ["planned"] = planned,
            ["completed"] = completed,
            ["completion_rate"] = new OrderedDictionary<string, double?>(completed.Select(c => KeyValuePair.Create(c.Key, Rate(c.Value, planned)))),
            ["outcome"] = outcome,
            ["outcome_rate"] = new OrderedDictionary<string, double?>(outcome.Select(o => KeyValuePair.Create(o.Key, Rate(o.Value, planned)))),
        };
        if (kind == "job")
        {
            var pulled = items.Where(i => Pulled.Contains(i.Outcomes.GetValueOrDefault("d0"))).ToList();
            var pulledD0 = new OrderedDictionary<string, int> { ["total"] = pulled.Count };
            foreach (var name in Outcomes.ReachedNames)
                pulledD0[name] = pulled.Count(i => i.Reached == name);
            pulledD0["prereqs_open"] = pulled.Count(i => i.PrereqsOpen == true);
            result["pulled_d0"] = pulledD0;
            result["pulled_d0_rate"] = Rate(pulled.Count, planned);
        }
        return result;
    }

    static OrderedDictionary<string, object?> ByKind(IEnumerable<Planned> items)
    {
        var list = items.ToList();
        return new OrderedDictionary<string, object?>(Outcomes.Kinds.Select(kind => KeyValuePair.Create(kind, (object?)Stats(list, kind))));
    }

    static OrderedDictionary<string, object?> With(OrderedDictionary<string, object?> head, OrderedDictionary<string, object?> tail)
    {
        foreach (var (key, value) in tail)
            head[key] = value;
        return head;
    }

    public static OrderedDictionary<string, object?> OutcomeKpis(
        IEnumerable<(DayOutcome Outcome, bool Provisional)> days,
        string? region = null,
        string? tech = null
    )
    {
        var series = new List<OrderedDictionary<string, object?>>();
        var pooled = new List<Planned>();
        var regions = new Dictionary<string, List<Planned>>();
        var techs = new Dictionary<string, List<Planned>>();
        var names = new Dictionary<string, string>();
        foreach (var (outcome, provisional) in days.OrderBy(d => d.Outcome.Day))
        {
            var items = outcome
                .Items.Where(i => (region is null || i.Region == region) && (tech is null || i.TechId == tech))
                .ToList();
            var entry = new OrderedDictionary<string, object?>
            {
                ["date"] = outcome.Day,
                ["status"] = outcome.Status,
                ["provisional"] = provisional,
            };
            series.Add(outcome.Status == "ok" ? With(entry, ByKind(items)) : entry);
            pooled.AddRange(items);
            foreach (var item in items)
            {
                Group(regions, item.Region).Add(item);
                Group(techs, item.TechId).Add(item);
                names.TryAdd(item.TechId, item.TechName);
            }
        }
        return new OrderedDictionary<string, object?>
        {
            ["days"] = series,
            ["totals"] = ByKind(pooled),
            ["by_region"] = regions
                .Keys.Order(StringComparer.Ordinal)
                .Select(name => With(new() { ["region"] = name }, ByKind(regions[name])))
                .ToList(),
            ["by_tech"] = techs
                .Keys.OrderBy(t => names[t], StringComparer.Ordinal)
                .ThenBy(t => t, StringComparer.Ordinal)
                .Select(t => With(new() { ["tech_id"] = t, ["tech_name"] = names[t] }, ByKind(techs[t])))
                .ToList(),
        };
    }

    static List<Planned> Group(Dictionary<string, List<Planned>> groups, string key) =>
        groups.TryGetValue(key, out var list) ? list : groups[key] = [];
}
