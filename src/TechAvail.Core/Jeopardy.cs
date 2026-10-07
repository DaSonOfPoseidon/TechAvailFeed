using TechAvail.Core.Parsing;

namespace TechAvail.Core;

// One job in jeopardy as of a snapshot. MinutesPast counts from JijAt.
public sealed record JeopardyRow(Block Job, DateTime JijAt, int MinutesPast);

// Job In Jeopardy (JIJ): a job on the day that still isn't complete once its scheduled end is
// Lead away. The population is the arrival report's: FIELD/TC work that was or is being worked.
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
