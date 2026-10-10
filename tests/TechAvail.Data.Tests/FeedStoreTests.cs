using TechAvail.Core;
using Dapper;
using Npgsql;
using TechAvail.Core.Parsing;

namespace TechAvail.Data.Tests;

public class FeedStoreTests
{
    static Block Job(string reference, string status = "A") =>
        new()
        {
            Kind = "job",
            WorkDate = new DateOnly(2026, 10, 6),
            TechId = "t1",
            TechName = "T1",
            StartsAt = new DateTime(2026, 10, 6, 9, 0, 0),
            EndsAt = new DateTime(2026, 10, 6, 11, 0, 0),
            RefId = reference,
            Status = status,
            Region = "North",
            Skills = "",
            Latitude = 40.5,
        };

    static ParsedFeed Blocks(params Block[] blocks)
    {
        var feed = new ParsedFeed
        {
            Sha256 = Guid.NewGuid().ToString("N"),
            Format = "blocks",
            GeneratedAt = new DateTimeOffset(2026, 10, 6, 6, 15, 0, TimeSpan.FromHours(-5)),
        };
        feed.Blocks.AddRange(blocks);
        return feed;
    }

    static long Save(FeedStore store, string id, ParsedFeed? feed, string? error = null) =>
        store.Save(id, "email", "TechAvailFeed", "f.csv", null, DateTimeOffset.UtcNow, feed, error: error);

    static List<BlockKey> At(NpgsqlConnection connection, long snapshot)
    {
        using var command = new NpgsqlCommand(
            $"SELECT {FeedStore.BlockColumns} FROM blocks WHERE {FeedStore.AtSnapshot} ORDER BY id",
            connection
        );
        command.Parameters.AddWithValue("snapshot", snapshot);
        using var reader = command.ExecuteReader();
        var rows = new List<BlockKey>();
        while (reader.Read())
            rows.Add(FeedStore.ReadKey(reader, 0));
        return rows;
    }

    // Every notification on FeedStore.ChangedChannel while act runs, once its transactions commit.
    static List<string> Notified(TestDatabase db, Action act)
    {
        using var listener = db.Open();
        var channels = new List<string>();
        listener.Notification += (_, e) => channels.Add(e.Channel);
        using (var listen = new NpgsqlCommand($"LISTEN {FeedStore.ChangedChannel}", listener))
            listen.ExecuteNonQuery();
        act();
        while (listener.Wait(TimeSpan.FromMilliseconds(500))) { }
        return channels;
    }

    [DbFact]
    public void Every_saved_snapshot_and_finalized_day_is_announced()
    {
        using var db = new TestDatabase();
        var store = new FeedStore(db.ConnectionString);
        Assert.Equal(
            [FeedStore.ChangedChannel, FeedStore.ChangedChannel, FeedStore.ChangedChannel],
            Notified(
                db,
                () =>
                {
                    Save(store, "<1>", Blocks(Job("a")));
                    Save(store, "<2>", null, error: "bad");
                    store.SaveDay(new DayOutcome(new DateOnly(2026, 10, 6), "no_morning"));
                }
            )
        );
    }

    [DbFact]
    public void Each_snapshot_is_reconstructable_from_the_diff()
    {
        using var db = new TestDatabase();
        var store = new FeedStore(db.ConnectionString);
        var first = Save(store, "<1>", Blocks(Job("a"), Job("b"), Job("b")));
        var second = Save(store, "<2>", Blocks(Job("b"), Job("a", status: "C"), Job("c")));
        var third = Save(store, "<3>", Blocks(Job("b"), Job("a", status: "C"), Job("c")));
        using var connection = db.Open();
        Assert.Equal(Sorted(Job("a"), Job("b"), Job("b")), Sorted(At(connection, first)));
        Assert.Equal(Sorted(Job("b"), Job("a", status: "C"), Job("c")), Sorted(At(connection, second)));
        Assert.Equal(Sorted(At(connection, second)), Sorted(At(connection, third)));
        // The unchanged third run wrote nothing; the second closed a and one b, and added two rows.
        Assert.Equal(5, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM blocks"));
        Assert.Equal(
            2,
            connection.ExecuteScalar<int>("SELECT COUNT(*) FROM blocks WHERE closed_snapshot_id = @second", new { second })
        );
    }

    static List<BlockKey> Sorted(params Block[] blocks) => Sorted(blocks.Select(BlockKey.From));

    static List<BlockKey> Sorted(IEnumerable<BlockKey> keys) => [.. keys.OrderBy(k => k.ToString(), StringComparer.Ordinal)];

    [DbFact]
    public void The_moved_sentinel_date_is_stored_as_a_date_not_infinity()
    {
        using var db = new TestDatabase();
        var store = new FeedStore(db.ConnectionString);
        var moved = Job("a") with
        {
            Kind = "job_moved",
            WorkDate = new DateOnly(9999, 12, 31),
            StartsAt = new DateTime(9999, 12, 31),
            EndsAt = new DateTime(9999, 12, 31),
        };
        Save(store, "<1>", Blocks(moved));
        using var connection = db.Open();
        Assert.Equal(
            ("9999-12-31", "9999-12-31 00:00:00"),
            connection.QuerySingle<(string, string)>("SELECT work_date::text, starts_at::text FROM blocks")
        );
    }

    [DbFact]
    public void Statuses_follow_the_feed()
    {
        using var db = new TestDatabase();
        var store = new FeedStore(db.ConnectionString);
        Save(store, "<ok>", Blocks(Job("a")));
        Save(store, "<empty>", Blocks());
        Save(store, "<error>", null, error: "missing columns: kind");
        using var connection = db.Open();
        var rows = connection
            .Query<(string, string, int, string?)>("SELECT message_id, status, row_count, error FROM snapshots ORDER BY id")
            .ToList();
        Assert.Equal(
            [("<ok>", "ok", 1, null), ("<empty>", "empty", 0, null), ("<error>", "error", 0, "missing columns: kind")],
            rows
        );
        // An empty run leaves the blocks of the last good one open.
        Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM current_blocks"));
        Assert.True(store.Seen("<empty>"));
        Assert.False(store.Seen("<other>"));
    }

    [DbFact]
    public void Slots_are_copied_per_snapshot()
    {
        using var db = new TestDatabase();
        var store = new FeedStore(db.ConnectionString);
        var feed = new ParsedFeed { Sha256 = "x" };
        feed.Slots.Add(
            new Slot(new DateOnly(2026, 9, 28), "t1", "T1", new DateTime(2026, 9, 28, 8, 0, 0), new DateTime(2026, 9, 28, 10, 30, 0), 150, "North", "CONN")
        );
        var id = Save(store, "<s>", feed);
        using var connection = db.Open();
        Assert.Equal(
            (id, 150, "slots"),
            connection.QuerySingle<(long, int, string)>(
                "SELECT s.snapshot_id, s.open_minutes, n.format FROM slots s JOIN snapshots n ON n.id = s.snapshot_id"
            )
        );
    }

    [DbFact]
    public void A_null_column_from_an_older_export_is_replaced()
    {
        using var db = new TestDatabase();
        var store = new FeedStore(db.ConnectionString);
        var first = Save(store, "<1>", Blocks(Job("a")));
        using var connection = db.Open();
        connection.Execute("UPDATE blocks SET department = NULL");
        var second = Save(store, "<2>", Blocks(Job("a")));
        Assert.Equal([second], connection.Query<long>("SELECT first_snapshot_id FROM current_blocks"));
        Assert.Equal([second], connection.Query<long>("SELECT closed_snapshot_id FROM blocks WHERE first_snapshot_id = @first", new { first }));
    }

    [DbFact]
    public void Set_region_round_trips_and_an_export_without_it_keeps_rows_open()
    {
        using var db = new TestDatabase();
        var store = new FeedStore(db.ConnectionString);
        var first = Save(store, "<1>", Blocks(Job("a")));
        Save(store, "<2>", Blocks(Job("a")));
        using var connection = db.Open();
        Assert.Equal([first], connection.Query<long>("SELECT first_snapshot_id FROM current_blocks"));
        Save(store, "<3>", Blocks(Job("a") with { SetRegion = "South" }));
        Assert.Equal("South", Assert.Single(store.Latest()!.Blocks!).SetRegion);
    }
}
