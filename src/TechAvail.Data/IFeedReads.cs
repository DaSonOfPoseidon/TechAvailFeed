using TechAvail.Core;
using TechAvail.Core.Parsing;

namespace TechAvail.Data;

public interface IFeedReads
{
    SnapshotMeta? SnapshotMeta();

    LatestSnapshot? Latest();

    List<(long Id, DateTimeOffset GeneratedAt)> BlocksSnapshots();

    List<Block> WorkBlocks(long snapshotId);

    Dictionary<string, string> ShiftRegions(long snapshotId, DateOnly day);

    HashSet<DateOnly> FinalizedDays();

    DayOutcome? LoadDay(DateOnly day);
}
