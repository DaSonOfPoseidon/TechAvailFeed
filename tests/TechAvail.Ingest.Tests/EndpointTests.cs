using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using TechAvail.Core.Parsing;
using TechAvail.Data;
using TechAvail.Data.Tests;

namespace TechAvail.Ingest.Tests;

public class EndpointTests
{
    sealed class Factory(TestDatabase db, string apiKey) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("DATABASE_URL", db.ConnectionString);
            builder.UseSetting("API_KEY", apiKey);
            // No IMAP settings: the worker serves HTTP only.
            builder.UseSetting("IMAP_USER", "");
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
        }
    }

    static void Seed(TestDatabase db)
    {
        var feed = new ParsedFeed { Sha256 = "x", Format = "blocks", GeneratedAt = DateTimeOffset.UtcNow };
        feed.Blocks.Add(
            new Block
            {
                Kind = "job",
                WorkDate = new DateOnly(2026, 10, 6),
                TechId = "a",
                TechName = "A",
                StartsAt = new DateTime(2026, 10, 6, 9, 0, 0),
                EndsAt = new DateTime(2026, 10, 6, 10, 0, 0),
                RefId = "1",
                Status = "A",
                Department = "FIELD",
                Region = "",
                Skills = "",
                Latitude = 40.123456,
                Longitude = -100.654321,
            }
        );
        new FeedStore(db.ConnectionString).Save("<1>", "email", null, null, null, DateTimeOffset.UtcNow, feed);
    }

    [DbFact]
    public async Task Health_needs_no_key_and_data_endpoints_do()
    {
        using var db = new TestDatabase();
        using var factory = new Factory(db, "s3cret");
        var client = factory.CreateClient();
        var health = JsonNode.Parse(await client.GetStringAsync("/health"))!;
        Assert.Equal((true, false), (health["ok"]!.GetValue<bool>(), health["mail_configured"]!.GetValue<bool>()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/runs.json")).StatusCode);
        client.DefaultRequestHeaders.Add("X-API-Key", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/runs.json")).StatusCode);
        client.DefaultRequestHeaders.Remove("X-API-Key");
        client.DefaultRequestHeaders.Add("X-API-Key", "s3cret");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/runs.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/latest.json")).StatusCode);
        var missing = await client.GetAsync("/nope");
        Assert.Equal("not found", JsonNode.Parse(await missing.Content.ReadAsStringAsync())!["error"]!.GetValue<string>());
        // No blocks snapshot yet: no days and no latest snapshot time.
        Assert.Equal("""{"latest_snapshot_at":null,"days":[]}""", JsonNode.Parse(await client.GetStringAsync("/history.json"))!.ToJsonString());
    }

    [DbFact]
    public async Task Latest_json_serves_a_slots_snapshot_as_stored()
    {
        using var db = new TestDatabase();
        var feed = new ParsedFeed { Sha256 = "x", GeneratedAt = DateTimeOffset.UtcNow };
        feed.Slots.Add(new Slot(new DateOnly(2026, 10, 6), "b", "Bea", new DateTime(2026, 10, 6, 13, 0, 0), new DateTime(2026, 10, 6, 15, 0, 0), 120, "North", "INS"));
        feed.Slots.Add(new Slot(new DateOnly(2026, 10, 6), "a", "Al", new DateTime(2026, 10, 6, 9, 0, 0), new DateTime(2026, 10, 6, 10, 0, 0), 60, "South", ""));
        new FeedStore(db.ConnectionString).Save("<1>", "email", null, null, null, DateTimeOffset.UtcNow, feed);
        using var factory = new Factory(db, "");
        var body = JsonNode.Parse(await factory.CreateClient().GetStringAsync("/latest.json"))!.AsObject();
        Assert.Equal(["snapshot", "slots"], body.Select(p => p.Key));
        Assert.Equal("slots", (string)body["snapshot"]!["format"]!);
        // Ordered by day, then tech name.
        var slots = body["slots"]!.AsArray();
        Assert.Equal(["Al", "Bea"], slots.Select(s => (string)s!["tech_name"]!));
        var bea = slots[1]!;
        Assert.Equal(
            ("b", "2026-10-06", "2026-10-06T13:00:00", "2026-10-06T15:00:00", 120, "North", "INS"),
            (
                (string)bea["tech_id"]!,
                (string)bea["work_date"]!,
                (string)bea["open_from"]!,
                (string)bea["open_until"]!,
                (int)bea["open_minutes"]!,
                (string)bea["region"]!,
                (string)bea["skills"]!
            )
        );
    }

    [DbFact]
    public async Task Latest_json_never_serves_exact_coordinates()
    {
        using var db = new TestDatabase();
        Seed(db);
        using var factory = new Factory(db, "");
        var body = JsonNode.Parse(await factory.CreateClient().GetStringAsync("/latest.json"))!;
        var job = body["blocks"]![0]!;
        Assert.Equal((40.123, -100.654), (job["latitude"]!.GetValue<double>(), job["longitude"]!.GetValue<double>()));
        Assert.Equal("<1>", body["snapshot"]!["message_id"]!.GetValue<string>());
    }
}
