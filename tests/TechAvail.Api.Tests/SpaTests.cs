using System.Net;
using System.Text.Json.Nodes;
using TechAvail.Data.Tests;

namespace TechAvail.Api.Tests;

public class SpaTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [DbFact]
    public async Task Client_routes_get_the_app_without_a_key()
    {
        using var db = new TestDatabase();
        using var factory = new ApiFactory(db, Now, apiKey: "secret");
        var client = factory.CreateClient();
        foreach (var path in new[] { "/", "/calendar", "/calendar/2026-10-06", "/kpis?region=North" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("<title>Tech Availability</title>", await response.Content.ReadAsStringAsync());
        }
    }

    [DbFact]
    public async Task Unknown_api_paths_and_missing_files_stay_404()
    {
        using var db = new TestDatabase();
        using var factory = new ApiFactory(db, Now);
        var client = factory.CreateClient();
        var api = await client.GetAsync("/api/v1/nope");
        Assert.Equal(HttpStatusCode.NotFound, api.StatusCode);
        Assert.NotEqual("text/html", api.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/main-MISSING.js")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api")).StatusCode);
        Assert.Equal("application/json", (await client.GetAsync("/health")).Content.Headers.ContentType?.MediaType);
        Assert.NotNull(JsonNode.Parse(await client.GetStringAsync("/openapi.json"))!["paths"]);
    }
}
