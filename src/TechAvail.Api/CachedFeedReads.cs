using System.ComponentModel;
using Microsoft.Extensions.Caching.Hybrid;
using TechAvail.Core;
using TechAvail.Core.Parsing;
using TechAvail.Data;

namespace TechAvail.Api;

// IFeedReads with a cache in front. A snapshot's rows never change once committed (FeedStore.AtSnapshot),
// so they're cached by snapshot id. What changes with each ingest (the snapshot list, finalized days)
// is keyed on FeedChanges' generation, and isn't cached while the listener is down.
// Cached values are shared between requests: callers must not modify them.
public sealed class CachedFeedReads(FeedStore store, FeedChanges changes, HybridCache cache) : IFeedReads
{
    // Boxed in a type marked immutable, so the in-memory cache hands back the same instance
    // instead of deserialising a copy on every hit.
    [ImmutableObject(true)]
    public sealed record Box<T>(T Value);

    static readonly HybridCacheEntryOptions BySnapshot = new() { Expiration = TimeSpan.FromHours(24) };

    // An old generation's entries are never read again; they only need to expire.
    static readonly HybridCacheEntryOptions ByGeneration = new() { Expiration = TimeSpan.FromHours(1) };

    // The store is synchronous, and a hit in the in-memory cache completes synchronously.
    T Get<T>(string key, Func<T> read, HybridCacheEntryOptions options) =>
        cache.GetOrCreateAsync(key, _ => ValueTask.FromResult(new Box<T>(read())), options).AsTask().GetAwaiter().GetResult().Value;

    public SnapshotMeta? SnapshotMeta() => changes.Current is { } state ? state.Served : store.SnapshotMeta();

    public LatestSnapshot? Latest() =>
        changes.Current is not { } state ? store.Latest()
        : state.Served is { } served ? Get($"snapshot:{served.Id}", () => store.Snapshot(served.Id), BySnapshot)
        : null;

    public List<(long Id, DateTimeOffset GeneratedAt)> BlocksSnapshots() =>
        changes.Current is { } state
            ? Get($"snapshots:{state.Generation}", store.BlocksSnapshots, ByGeneration)
            : store.BlocksSnapshots();

    public List<Block> WorkBlocks(long snapshotId) => Get($"work:{snapshotId}", () => store.WorkBlocks(snapshotId), BySnapshot);

    public Dictionary<string, string> ShiftRegions(long snapshotId, DateOnly day) =>
        Get($"regions:{snapshotId}:{day:yyyy-MM-dd}", () => store.ShiftRegions(snapshotId, day), BySnapshot);

    public HashSet<DateOnly> FinalizedDays() =>
        changes.Current is { } state
            ? Get($"finalized:{state.Generation}", store.FinalizedDays, ByGeneration)
            : store.FinalizedDays();

    // A finalized day is written once and never changes; a day that isn't finalized has nothing to load.
    public DayOutcome? LoadDay(DateOnly day) =>
        changes.Current is null ? store.LoadDay(day)
        : FinalizedDays().Contains(day) ? Get($"day:{day:yyyy-MM-dd}", () => store.LoadDay(day), BySnapshot)
        : null;
}
