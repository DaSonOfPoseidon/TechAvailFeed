using System.Net;
using System.Text.Json.Nodes;
using TechAvail.Core.Parsing;
using TechAvail.Data;
using TechAvail.Data.Tests;

namespace TechAvail.Api.Tests;

public class HealthTests
{
    static readonly DateTimeOffset Generated = new(2026, 10, 6, 6, 15, 0, TimeSpan.FromHours(-5));

    static void SaveSnapshot(TestDatabase db)
    {
        var feed = new ParsedFeed { Sha256 = "x", Format = "blocks", GeneratedAt = Generated };
        feed.Blocks.Add(
            new Block
            {
                Kind = "shift",
                WorkDate = new DateOnly(2026, 10, 6),
                TechId = "t1",
                TechName = "T1",
                StartsAt = new DateTime(2026, 10, 6, 8, 0, 0),
                EndsAt = new DateTime(2026, 10, 6, 17, 0, 0),
                RefId = "",
                Status = "",
                Region = "North",
                Skills = "INS",
            }
        );
        new FeedStore(db.ConnectionString).Save("<1>", "email", null, null, null, Generated, feed);
    }

    [DbFact]
    public async Task Health_needs_no_key_and_reports_snapshot_age()
    {
        using var db = new TestDatabase();
        using var factory = new ApiFactory(db, Generated.AddMinutes(50), apiKey: "secret");
        var client = factory.CreateClient();
        var empty = JsonNode.Parse(await client.GetStringAsync("/health"))!;
        Assert.True(empty["ok"]!.GetValue<bool>());
        Assert.Null(empty["snapshot"]);

        SaveSnapshot(db);
        var snapshot = JsonNode.Parse(await client.GetStringAsync("/health"))!["snapshot"]!;
        Assert.Equal(50.0, snapshot["age_min"]!.GetValue<double>());
        Assert.True(snapshot["stale"]!.GetValue<bool>());
        Assert.Equal(Generated, snapshot["generated_at"]!.GetValue<DateTimeOffset>());
    }

    [DbFact]
    public async Task Api_routes_need_the_key_when_one_is_set()
    {
        using var db = new TestDatabase();
        using var factory = new ApiFactory(db, Generated, apiKey: "secret");
        var client = factory.CreateClient();
        var missing = await client.GetAsync("/api/v1/filters");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal("missing or wrong X-API-Key", JsonNode.Parse(await missing.Content.ReadAsStringAsync())!["detail"]!.GetValue<string>());
        client.DefaultRequestHeaders.Add("X-API-Key", "secret");
        // Past the key check: no snapshot yet.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/filters")).StatusCode);
    }
}
