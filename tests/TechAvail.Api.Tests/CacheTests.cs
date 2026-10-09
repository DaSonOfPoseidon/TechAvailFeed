using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TechAvail.Core.Parsing;
using TechAvail.Data;
using TechAvail.Data.Tests;

namespace TechAvail.Api.Tests;

public class CacheTests
{
    static readonly DateOnly Day = new(2026, 10, 6);
    static readonly DateTimeOffset Now = new(2026, 10, 6, 7, 0, 0, TimeSpan.FromHours(-5));

    static long Save(FeedStore store, string id, string tech)
    {
        var feed = new ParsedFeed { Sha256 = id, Format = "blocks", GeneratedAt = Now };
        feed.Blocks.Add(
            new Block
            {
                Kind = "job",
                WorkDate = Day,
                TechId = tech,
                TechName = tech,
                StartsAt = Day.ToDateTime(new TimeOnly(9, 0)),
                EndsAt = Day.ToDateTime(new TimeOnly(11, 0)),
                RefId = "j1",
                Status = "A",
                Region = "North",
                Skills = "",
            }
        );
        return store.Save(id, "email", null, null, null, Now, feed);
    }

    // The listener runs in the background: wait for it to reach a state.
    static async Task Until(Func<bool> done)
    {
        for (var waited = 0; !done(); waited += 20)
        {
            Assert.True(waited < 10_000, "timed out waiting for the feed listener");
            await Task.Delay(20);
        }
    }

    static long? Served(ApiFactory factory) => factory.Services.GetRequiredService<FeedChanges>().Current?.Served?.Id;

    [DbFact]
    public async Task Reads_are_cached_until_the_ingest_announces_a_change()
    {
        using var db = new TestDatabase();
        var store = new FeedStore(db.ConnectionString);
        var first = Save(store, "<1>", "T1");
        using var factory = new ApiFactory(db, Now);
        var reads = factory.Services.GetRequiredService<IFeedReads>();
        await Until(() => Served(factory) == first);

        var latest = reads.Latest()!;
        Assert.Equal("T1", Assert.Single(latest.Blocks!).TechName);
        Assert.Same(latest, reads.Latest());
        Assert.Same(reads.WorkBlocks(first), reads.WorkBlocks(first));

        // A change nobody announces isn't seen: the reads didn't go back to Postgres.
        using (var connection = db.Open())
        using (var command = new NpgsqlCommand("UPDATE blocks SET tech_name = 'changed'", connection))
            command.ExecuteNonQuery();
        Assert.Equal("T1", Assert.Single(reads.Latest()!.Blocks!).TechName);

        // A new snapshot is announced, and served from then on.
        var second = Save(store, "<2>", "T2");
        await Until(() => Served(factory) == second);
        Assert.Equal("T2", Assert.Single(reads.Latest()!.Blocks!).TechName);
        Assert.Equal([first, second], reads.BlocksSnapshots().Select(s => s.Id));
    }

    [DbFact]
    public async Task Reads_go_to_postgres_while_the_listener_is_down_and_it_reconnects()
    {
        using var db = new TestDatabase();
        var store = new FeedStore(db.ConnectionString);
        var first = Save(store, "<1>", "T1");
        using var factory = new ApiFactory(db, Now);
        var reads = factory.Services.GetRequiredService<IFeedReads>();
        await Until(() => Served(factory) == first);
        Assert.Equal([first], reads.BlocksSnapshots().Select(s => s.Id));

        using (var connection = db.Open())
        using (
            var command = new NpgsqlCommand(
                "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = current_database() AND query LIKE 'LISTEN%'",
                connection
            )
        )
            command.ExecuteNonQuery();
        await Until(() => factory.Services.GetRequiredService<FeedChanges>().Current is null);

        var second = Save(store, "<2>", "T2");
        Assert.Equal(second, reads.Latest()!.Snapshot.Id);
        Assert.Equal([first, second], reads.BlocksSnapshots().Select(s => s.Id));

        // The retry timer may not exist yet when the listener is seen down, so keep the clock moving.
        await Until(() =>
        {
            factory.Clock.Advance(FeedChanges.RetryDelay);
            return Served(factory) == second;
        });
    }
}
