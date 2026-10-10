using System.Text.Json.Serialization;
using TechAvail.Core;

namespace TechAvail.Api;

// Counts and rates for one kind of planned work. Rates are over the planned count, rounded to 3
// places, and null when nothing was planned. Completed and its rate are keyed by checkpoint;
// outcome and its rate by outcome name.
public record KindStats(
    int Planned,
    Dictionary<string, int> Completed,
    Dictionary<string, double?> CompletionRate,
    Dictionary<string, int> Outcome,
    Dictionary<string, double?> OutcomeRate
);

// Jobs also count the ones pulled on the plan day (canceled, unscheduled or rescheduled by d0),
// split by how far the tech got.
public sealed record JobStats(
    int Planned,
    Dictionary<string, int> Completed,
    Dictionary<string, double?> CompletionRate,
    Dictionary<string, int> Outcome,
    Dictionary<string, double?> OutcomeRate,
    PulledCounts PulledD0,
    double? PulledD0Rate
) : KindStats(Planned, Completed, CompletionRate, Outcome, OutcomeRate);

public sealed record ByKind(JobStats Job, KindStats Ticket);

// A day without a morning plan is listed with its status but no stats.
public sealed record DayKpis(
    DateOnly Date,
    string Status,
    bool Provisional,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JobStats? Job,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] KindStats? Ticket
);

public sealed record RegionKpis(string Region, JobStats Job, KindStats Ticket);

public sealed record TechKpis(string TechId, string TechName, JobStats Job, KindStats Ticket);

public sealed record TechOption(string TechId, string TechName);

// Techs: everyone with planned work in the range and region, whatever the tech filter, so a
// technician picker can offer them (a former tech has history but no shift).
public sealed record OutcomeKpis(
    List<DayKpis> Days,
    ByKind Totals,
    List<RegionKpis> ByRegion,
    List<TechKpis> ByTech,
    List<TechOption> Techs
);

// Rates over the outcome history's planned items. Each rate's denominator is the planned count of
// that kind; a day without a morning plan is listed but adds nothing.
public static class Kpis
{
    // The latest checkpoint the item has: d2 once a day is final, the outcome so far before that.
    static string? Final(Planned item) =>
        Outcomes.Checkpoints.Reverse().Select(item.Outcomes.GetValueOrDefault).FirstOrDefault(o => o is not null);

    static double? Rate(int count, int total) => total != 0 ? Math.Round((double)count / total, 3) : null;

    static Dictionary<string, double?> Rates(Dictionary<string, int> counts, int total) =>
        counts.ToDictionary(c => c.Key, c => Rate(c.Value, total));

    static KindStats Stats(List<Planned> items)
    {
        var completed = Outcomes.Checkpoints.ToDictionary(
            name => name,
            name => items.Count(i => i.Outcomes.GetValueOrDefault(name) == "completed")
        );
        var latest = items.Select(Final).ToList();
        var outcome = Outcomes.OutcomeNames.ToDictionary(name => name, name => latest.Count(l => l == name));
        return new KindStats(items.Count, completed, Rates(completed, items.Count), outcome, Rates(outcome, items.Count));
    }

    static ByKind Kinds(IEnumerable<Planned> items)
    {
        var list = items.ToList();
        var jobs = list.Where(i => i.Kind == "job").ToList();
        var stats = Stats(jobs);
        var pulled = Outcomes.PulledCounts(jobs);
        var job = new JobStats(
            stats.Planned,
            stats.Completed,
            stats.CompletionRate,
            stats.Outcome,
            stats.OutcomeRate,
            pulled,
            Rate(pulled.Total, jobs.Count)
        );
        return new ByKind(job, Stats([.. list.Where(i => i.Kind == "ticket")]));
    }

    public static OutcomeKpis OutcomeKpis(IEnumerable<(DayOutcome Outcome, bool Provisional)> days, string? region = null, string? tech = null)
    {
        var series = new List<DayKpis>();
        var pooled = new List<Planned>();
        var techs = new Dictionary<string, string>();
        foreach (var (outcome, provisional) in days.OrderBy(d => d.Outcome.Day))
        {
            foreach (var item in outcome.Items.Where(i => region is null || i.Region == region))
                techs.TryAdd(item.TechId, item.TechName);
            var items = outcome.Items.Where(i => (region is null || i.Region == region) && (tech is null || i.TechId == tech)).ToList();
            var kinds = outcome.Status == "ok" ? Kinds(items) : null;
            series.Add(new DayKpis(outcome.Day, outcome.Status, provisional, kinds?.Job, kinds?.Ticket));
            pooled.AddRange(items);
        }
        var names = pooled.GroupBy(i => i.TechId).ToDictionary(g => g.Key, g => g.First().TechName);
        return new OutcomeKpis(
            series,
            Kinds(pooled),
            [
                .. pooled
                    .GroupBy(i => i.Region)
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g =>
                    {
                        var kinds = Kinds(g);
                        return new RegionKpis(g.Key, kinds.Job, kinds.Ticket);
                    }),
            ],
            [
                .. pooled
                    .GroupBy(i => i.TechId)
                    .OrderBy(g => names[g.Key], StringComparer.Ordinal)
                    .ThenBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g =>
                    {
                        var kinds = Kinds(g);
                        return new TechKpis(g.Key, names[g.Key], kinds.Job, kinds.Ticket);
                    }),
            ],
            [
                .. techs
                    .Select(t => new TechOption(t.Key, t.Value))
                    .OrderBy(t => t.TechName, StringComparer.Ordinal)
                    .ThenBy(t => t.TechId, StringComparer.Ordinal),
            ]
        );
    }
}
