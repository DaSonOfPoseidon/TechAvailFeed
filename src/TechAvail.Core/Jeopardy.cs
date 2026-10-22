using TechAvail.Core.Parsing;

namespace TechAvail.Core;

public sealed record JeopardyRow(Block Job, DateTime JijAt, int MinutesPast);

// Rules: docs/business-rules.md#jeopardy-and-the-status-update
public static class Jeopardy
{
    public static readonly TimeSpan Lead = TimeSpan.FromMinutes(30);

    public static DateTime At(Block job) => job.EndsAt - Lead;

    public static List<JeopardyRow> Find(IEnumerable<Block> blocks, DateOnly day, DateTime asOf) =>
    [
        .. Arrivals
            .Scope(blocks, day)
            .Where(b => !Outcomes.CompletedStatuses[b.Kind].Contains(b.Status) && At(b) <= asOf)
            .Select(b => new JeopardyRow(b, At(b), (int)Math.Floor((asOf - At(b)).TotalMinutes)))
            .OrderBy(r => r.JijAt)
            .ThenBy(r => r.Job.TechName, StringComparer.Ordinal)
            .ThenBy(r => r.Job.RefId, StringComparer.Ordinal),
    ];
}
