using TechAvail.Core.Parsing;

namespace TechAvail.Data;

// The content of one blocks row, as stored: text columns can be NULL in rows from older exports,
// and NULL never equals "" (a row read back has to match exactly to stay open). Field order is
// the table's column order.
public sealed record BlockKey(
    string Kind,
    DateOnly WorkDate,
    string TechId,
    string TechName,
    DateTime StartsAt,
    DateTime EndsAt,
    string? RefId,
    string? Status,
    string? Department,
    string? Region,
    string? Skills,
    string? TaskType,
    DateTime? ModifiedAt,
    string? ModifiedBy,
    DateTime? EnrouteAt,
    DateTime? InprogressAt,
    string? PrereqsStatus,
    string? AddressIssue,
    double? Latitude,
    double? Longitude,
    string? GpsPrecision
)
{
    public static BlockKey From(Block b) =>
        new(
            b.Kind,
            b.WorkDate,
            b.TechId,
            b.TechName,
            b.StartsAt,
            b.EndsAt,
            b.RefId,
            b.Status,
            b.Department,
            b.Region,
            b.Skills,
            b.TaskType,
            b.ModifiedAt,
            b.ModifiedBy,
            b.EnrouteAt,
            b.InprogressAt,
            b.PrereqsStatus,
            b.AddressIssue,
            b.Latitude,
            b.Longitude,
            b.GpsPrecision
        );
}

public static class BlockDiff
{
    // What turns the current rows into the new snapshot, as a multiset: the rows to insert (in
    // snapshot order) and the ids to close (in current-row order). An unchanged row stays open.
    public static (List<BlockKey> Insert, List<long> Close) Diff(
        IReadOnlyList<(long Id, BlockKey Key)> current,
        IReadOnlyList<BlockKey> next
    )
    {
        var wanted = new Dictionary<BlockKey, int>();
        foreach (var key in next)
            wanted[key] = wanted.GetValueOrDefault(key) + 1;
        var close = new List<long>();
        foreach (var (id, key) in current)
        {
            if (wanted.TryGetValue(key, out var count) && count > 0)
                wanted[key] = count - 1;
            else
                close.Add(id);
        }
        var insert = new List<BlockKey>();
        foreach (var key in next)
        {
            if (wanted.TryGetValue(key, out var count) && count > 0)
            {
                wanted[key] = count - 1;
                insert.Add(key);
            }
        }
        return (insert, close);
    }
}
