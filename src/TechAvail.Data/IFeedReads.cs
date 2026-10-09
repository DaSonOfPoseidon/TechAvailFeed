using TechAvail.Core;
using TechAvail.Core.Parsing;

namespace TechAvail.Data;

// The reads the dashboard serves from. FeedStore answers them from Postgres; the API puts a cache in front.
public interface IFeedReads
{
    // The served snapshot (see FeedStore.Latest), without its rows.
    SnapshotMeta? SnapshotMeta();

    LatestSnapshot? Latest();

    List<(long Id, DateTimeOffset GeneratedAt)> BlocksSnapshots();

    List<Block> WorkBlocks(long snapshotId);

    Dictionary<string, string> ShiftRegions(long snapshotId, DateOnly day);

    HashSet<DateOnly> FinalizedDays();

    DayOutcome? LoadDay(DateOnly day);
}
