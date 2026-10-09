using TechAvail.Core;
using TechAvail.Core.Parsing;

namespace TechAvail.Data;

public sealed record HistoryDay(
    DateOnly Date,
    string Status,
    bool Provisional,
    DateTime? MorningAt,
    DaySummary? ByKind
);

public sealed record HistoryResult(DateTime? LatestSnapshotAt, List<HistoryDay> Days);

// The outcome rules wired to the store. Days whose d2 has ended are
// written once (outcome_days / job_outcomes) and never recomputed; later days are computed live.
public sealed class OutcomeHistory(FeedStore store, TimeZoneInfo tz, TimeProvider? clock = null)
{
    // A day is final once D+2 has ended: its d2 checkpoint can no longer change.
    static readonly TimeSpan FinalAfter = TimeSpan.FromDays(3);
    public const int HistoryDays = 60;

    readonly TimeProvider clock = clock ?? TimeProvider.System;

    public List<Snapshot> Snapshots() =>
        [.. store.BlocksSnapshots().Select(s => new Snapshot(s.Id, TimeZoneInfo.ConvertTime(s.GeneratedAt, tz).DateTime))];

    DayOutcome ComputeDay(List<Snapshot> snaps, DateOnly day, Dictionary<long, List<Block>> cache)
    {
        List<Block> Blocks(Snapshot snapshot)
        {
            if (!cache.TryGetValue(snapshot.Id, out var blocks))
                cache[snapshot.Id] = blocks = store.WorkBlocks(snapshot.Id);
            return blocks;
        }

        var morning = Outcomes.FindMorning(snaps, day);
        if (morning is null)
            return Outcomes.DayOutcome(day, null, [], new Dictionary<string, IReadOnlyList<Block>?>());
        var checkpoints = new Dictionary<string, IReadOnlyList<Block>?>();
        for (int offset = 0; offset < Outcomes.Checkpoints.Length; offset++)
        {
            var checkpoint = Outcomes.FindCheckpoint(snaps, morning, day, offset);
            checkpoints[Outcomes.Checkpoints[offset]] = checkpoint is null ? null : Blocks(checkpoint);
        }
        var regions = store.ShiftRegions(morning.Id, day);
        return Outcomes.DayOutcome(day, morning, Blocks(morning), checkpoints, regions);
    }

    static bool IsFinal(DateOnly day, DateTime latest) => day.ToDateTime(TimeOnly.MinValue) + FinalAfter <= latest;

    // Persist every day whose d2 has passed, so the history survives snapshot pruning.
    public int Finalize()
    {
        var snaps = Snapshots();
        if (snaps.Count == 0)
            return 0;
        var done = store.FinalizedDays();
        var latest = snaps[^1].At;
        var cache = new Dictionary<long, List<Block>>();
        int written = 0;
        for (var day = DateOnly.FromDateTime(snaps[0].At); IsFinal(day, latest); day = day.AddDays(1))
        {
            if (done.Contains(day))
                continue;
            store.SaveDay(ComputeDay(snaps, day, cache));
            written++;
        }
        return written;
    }

    // Each day in [start, end], newest first, with whether it is still provisional (computed live
    // because D+2 hasn't ended). Days before the first blocks snapshot are left out.
    public List<(DayOutcome Outcome, bool Provisional)> Range(DateOnly start, DateOnly end)
    {
        var snaps = Snapshots();
        if (snaps.Count == 0)
            return [];
        var first = DateOnly.FromDateTime(snaps[0].At);
        var cache = new Dictionary<long, List<Block>>();
        var result = new List<(DayOutcome, bool)>();
        for (var day = end; day >= (start > first ? start : first); day = day.AddDays(-1))
        {
            var outcome = store.LoadDay(day);
            result.Add(outcome is null ? (ComputeDay(snaps, day, cache), true) : (outcome, false));
        }
        return result;
    }

    public HistoryResult History(int days = HistoryDays)
    {
        var snaps = Snapshots();
        if (snaps.Count == 0)
            return new HistoryResult(null, []);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), tz).DateTime);
        var result = Range(today.AddDays(-(days - 1)), today)
            .Select(r => new HistoryDay(
                r.Outcome.Day,
                r.Outcome.Status,
                r.Provisional,
                r.Outcome.Morning?.At,
                r.Outcome.Status == "ok" ? Outcomes.Summarise(r.Outcome) : null
            ))
            .ToList();
        return new HistoryResult(snaps[^1].At, result);
    }
}
