using TechAvail.Data;

namespace TechAvail.Api;

public sealed record SnapshotInfo(long Id, DateTimeOffset? GeneratedAt, double? AgeMin, bool Stale);

public static class Snapshots
{
    // The source sends every 15 minutes, so three missed runs means the numbers are going stale.
    public const int StaleMinutes = 45;

    public static SnapshotInfo? Info(SnapshotMeta? meta, DateTimeOffset now)
    {
        if (meta is null)
            return null;
        double? age = meta.GeneratedAt is { } generated ? Core.PyMath.Round((now - generated).TotalSeconds / 60, 1) : null;
        return new SnapshotInfo(meta.Id, meta.GeneratedAt, age, age is null || age > StaleMinutes);
    }

    public static SnapshotInfo? Info(SnapshotRow snapshot, DateTimeOffset now) =>
        Info(new SnapshotMeta(snapshot.Id, snapshot.Format, snapshot.GeneratedAt, snapshot.IngestedAt), now);
}
