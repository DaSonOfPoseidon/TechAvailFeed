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
            // Revalidated, so a deploy's renamed bundles are picked up on the next load.
            Assert.True(response.Headers.CacheControl?.NoCache);
            Assert.Contains("<title>Tech Availability</title>", await response.Content.ReadAsStringAsync());
        }
    }

    // The exports end in .xlsx, which Cloudflare caches by default: without no-store the tunnel keeps
    // serving an old workbook after an ingest or a deploy.
    [DbFact]
    public async Task Api_responses_are_never_cached()
    {
        using var db = new TestDatabase();
        using var factory = new ApiFactory(db, Now, apiKey: "secret");
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "secret");
        foreach (var path in new[] { "/api/v1/capacity.xlsx", "/api/v1/filters", "/api/v1/nope", "/health" })
            Assert.True((await client.GetAsync(path)).Headers.CacheControl?.NoStore, path);
        // Unauthorised answers too, so a cache never keeps a 401 for a URL.
        Assert.True((await factory.CreateClient().GetAsync("/api/v1/filters")).Headers.CacheControl?.NoStore);
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
        // Route constraints ignore case, like the controllers' routes.
        foreach (var path in new[] { "/api", "/API/v1/nope", "/Api" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        Assert.Equal("application/json", (await client.GetAsync("/health")).Content.Headers.ContentType?.MediaType);
        Assert.NotNull(JsonNode.Parse(await client.GetStringAsync("/openapi.json"))!["paths"]);
    }
}
