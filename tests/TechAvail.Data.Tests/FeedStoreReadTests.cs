using TechAvail.Core;
using TechAvail.Core.Parsing;

namespace TechAvail.Data.Tests;

public class FeedStoreReadTests
{
    static readonly DateOnly Day = new(2026, 10, 6);

    static Block B(string kind, string reference, string tech = "t1", string region = "North", DateOnly? day = null) =>
        new()
        {
            Kind = kind,
            WorkDate = day ?? Day,
            TechId = tech,
            TechName = tech.ToUpperInvariant(),
            StartsAt = (day ?? Day).ToDateTime(new TimeOnly(9, 0)),
            EndsAt = (day ?? Day).ToDateTime(new TimeOnly(11, 0)),
            RefId = reference,
            Status = "A",
            Region = region,
            Skills = "",
        };

    static ParsedFeed Blocks(DateTimeOffset generatedAt, params Block[] blocks)
    {
        var feed = new ParsedFeed { Sha256 = "x", Format = "blocks", GeneratedAt = generatedAt };
        feed.Blocks.AddRange(blocks);
        return feed;
    }

    static ParsedFeed Slots()
    {
        var feed = new ParsedFeed { Sha256 = "s" };
        feed.Slots.Add(new Slot(Day, "t1", "T1", Day.ToDateTime(new TimeOnly(8, 0)), Day.ToDateTime(new TimeOnly(9, 0)), 60, "", ""));
        return feed;
    }

    static readonly DateTimeOffset Six = new(2026, 10, 6, 6, 15, 0, TimeSpan.FromHours(-5));

    static long Save(FeedStore store, string id, ParsedFeed feed) =>
        store.Save(id, "email", null, null, null, Six.AddMinutes(1), feed);

    [DbFact]
    public void Latest_serves_the_newest_blocks_snapshot_over_a_newer_legacy_or_empty_one()
    {
        using var db = new TestDatabase();
        var store = new FeedStore(db.ConnectionString);
        Assert.Null(store.Latest());
        var blocks = Save(store, "<b>", Blocks(Six, B("job", "1"), B("shift", "")));
        Save(store, "<s>", Slots());
        Save(store, "<e>", Blocks(Six));
        var latest = store.Latest()!;
        Assert.Equal(blocks, latest.Snapshot.Id);
        Assert.Equal(Six, latest.Snapshot.GeneratedAt);
        Assert.Equal(["job", "shift"], latest.Blocks!.Select(b => b.Kind).Order());
        Assert.Equal(blocks, store.SnapshotMeta()!.Id);
        Assert.Equal([(blocks, Six)], store.BlocksSnapshots());
        Assert.Equal(["1"], store.WorkBlocks(blocks).Select(b => b.RefId));
        var latency = store.Latency();
        Assert.Equal((3L, 2L, 1L, 0L), (latency.Runs, latency.Ok, latency.Empty, latency.Errors));
        Assert.Equal(3, store.Runs().Count);
        Assert.Equal(1m, store.Runs()[^1].MbsToMailboxMin);
    }

    [DbFact]
    public void Shift_regions_prefer_the_install_shift()
    {
        using var db = new TestDatabase();
        var store = new FeedStore(db.ConnectionString);
        var id = Save(store, "<1>", Blocks(Six, B("shift_tc", "", region: "TC"), B("shift", "", region: "North"), B("shift_tc", "", tech: "t2", region: "TC")));
        Assert.Equal(new Dictionary<string, string> { ["t1"] = "North", ["t2"] = "TC" }, store.ShiftRegions(id, Day));
    }

    [DbFact]
    public void A_saved_day_loads_back_unchanged()
    {
        using var db = new TestDatabase();
        var store = new FeedStore(db.ConnectionString);
        var morning = new Snapshot(Save(store, "<1>", Blocks(Six, B("job", "1"))), new DateTime(2026, 10, 6, 6, 15, 0));
        var item = new Planned("job", "1", "t1", "T1", Day.ToDateTime(new TimeOnly(9, 0)))
        {
            Outcomes = new() { ["d0"] = "completed", ["d1"] = "completed", ["d2"] = "completed" },
            Reached = "in_progress",
            PrereqsOpen = false,
            Region = "North",
            TaskType = "3",
        };
        store.SaveDay(new DayOutcome(Day, "ok", morning, [item], new() { ["job"] = 2, ["ticket"] = 0 }));
        store.SaveDay(new DayOutcome(Day.AddDays(1), "no_morning"));
        // Written once: saving the day again changes nothing.
        store.SaveDay(new DayOutcome(Day, "no_morning"));
        Assert.Equal([Day, Day.AddDays(1)], store.FinalizedDays().Order());
        var loaded = store.LoadDay(Day)!;
        Assert.Equal(("ok", morning, 2), (loaded.Status, loaded.Morning, loaded.AddedAfterMorning["job"]));
        var got = Assert.Single(loaded.Items);
        Assert.Equal(
            ("1", "in_progress", (bool?)false, "North", "3", item.PlannedStart),
            (got.RefId, got.Reached, got.PrereqsOpen, got.Region, got.TaskType, got.PlannedStart)
        );
        Assert.Equal(item.Outcomes, got.Outcomes);
        Assert.Null(store.LoadDay(Day.AddDays(1))!.Morning);
        Assert.Null(store.LoadDay(Day.AddDays(2)));
    }
}
