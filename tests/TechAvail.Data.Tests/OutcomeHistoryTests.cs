using Microsoft.Extensions.Time.Testing;
using TechAvail.Core.Parsing;

namespace TechAvail.Data.Tests;

public class OutcomeHistoryTests
{
    static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    static readonly DateOnly Day = new(2026, 10, 6);

    static Block Job(string status) =>
        new()
        {
            Kind = "job",
            WorkDate = Day,
            TechId = "t1",
            TechName = "T1",
            StartsAt = Day.ToDateTime(new TimeOnly(9, 0)),
            EndsAt = Day.ToDateTime(new TimeOnly(11, 0)),
            RefId = "1",
            Status = status,
            Department = "FIELD",
            Region = "North",
            Skills = "",
        };

    static void Save(FeedStore store, DateTime local, string status)
    {
        var at = new DateTimeOffset(local, Chicago.GetUtcOffset(local));
        var feed = new ParsedFeed { Sha256 = "x", Format = "blocks", GeneratedAt = at };
        feed.Blocks.Add(Job(status));
        store.Save($"<{local:O}>", "email", null, null, null, at, feed);
    }

    [DbFact]
    public void Days_are_finalized_once_their_d2_has_ended()
    {
        using var db = new TestDatabase();
        var store = new FeedStore(db.ConnectionString);
        Save(store, Day.ToDateTime(new TimeOnly(6, 15)), "A");
        Save(store, Day.ToDateTime(new TimeOnly(20, 0)), "C");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 8, 17, 0, 0, TimeSpan.Zero));
        var history = new OutcomeHistory(store, Chicago, clock);

        // D+2 hasn't ended: nothing is written, the day is computed live.
        Assert.Equal(0, history.Finalize(store.SaveDay));
        var live = history.History(days: 3);
        Assert.Equal([true, true, true], live.Days.Select(d => d.Provisional));
        var planDay = live.Days.Single(d => d.Date == Day);
        Assert.Equal("ok", planDay.Status);
        Assert.Equal((1, 1), (planDay.ByKind!.Job.Planned, planDay.ByKind.Job.CompletedD0));

        // A snapshot after D+2 makes the day final; it is written once.
        Save(store, Day.AddDays(3).ToDateTime(new TimeOnly(0, 15)), "C");
        Assert.Equal(1, history.Finalize(store.SaveDay));
        Assert.Equal(0, history.Finalize(store.SaveDay));
        Assert.Equal([Day], store.FinalizedDays());
        var (outcome, provisional) = history.Range(Day, Day).Single();
        Assert.False(provisional);
        Assert.Equal(["completed", "completed", "completed"], outcome.Items.Single().Outcomes.Values);
    }
}
