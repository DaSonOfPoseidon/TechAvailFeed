using System.Text.Json;
using TechAvail.Api;
using TechAvail.Api.Controllers;
using TechAvail.Data;

// Dashboard API: JSON for the calendar and KPI charts, the Excel exports and the dashboard itself.
// Read-only; the ingest service writes everything.
if (args.Contains("--healthcheck"))
{
    // For the container healthcheck: the aspnet image has no curl.
    var port = Environment.GetEnvironmentVariable("HTTP_PORT") ?? "8000";
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    try
    {
        return (await http.GetAsync($"http://localhost:{port}/health")).IsSuccessStatusCode ? 0 : 1;
    }
    catch (HttpRequestException)
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{builder.Configuration["HTTP_PORT"] ?? "8000"}");
builder.Services.AddSingleton(services => ApiSettings.From(services.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(services => new FeedStore(services.GetRequiredService<ApiSettings>().ConnectionString));
builder.Services.AddSingleton<FeedChanges>();
builder.Services.AddHostedService(services => services.GetRequiredService<FeedChanges>());
// Payloads are only serialised if a second-level cache (e.g. Redis) is added; a snapshot's rows are a few MB.
builder.Services.AddHybridCache(options => options.MaximumPayloadBytes = 64 * 1024 * 1024);
builder.Services.AddSingleton<IFeedReads, CachedFeedReads>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddCors();
builder.Services
    .AddControllers(options => options.Filters.Add<ApiExceptionFilter>())
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    });
// Bad query values are a 422 (ASP.NET Core would answer 400), with the usual {"detail": ...} body.
builder.Services.Configure<Microsoft.AspNetCore.Mvc.ApiBehaviorOptions>(options =>
    options.InvalidModelStateResponseFactory = context =>
        Errors.Detail(422, string.Join("; ", context.ModelState.Where(e => e.Value?.Errors.Count > 0).Select(e => $"{e.Key}: invalid value")))
);
builder.Services.AddOpenApi();

var app = builder.Build();
var settings = app.Services.GetRequiredService<ApiSettings>();
if (settings.CorsOrigins.Length > 0)
    app.UseCors(cors =>
        cors.WithOrigins(settings.CorsOrigins).WithMethods("GET").WithHeaders("X-API-Key").WithExposedHeaders("Content-Disposition")
    );
app.MapOpenApi("/openapi.json");
// The dashboard (web/, built into wwwroot by the Dockerfile). Its files need no key; the data does.
// index.html names the build's hashed bundles, so browsers must revalidate it: a cached copy
// would point a tab at bundles the last deploy removed.
var dashboardFiles = new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        if (context.File.Name.EndsWith(".html", StringComparison.Ordinal))
            context.Context.Response.Headers.CacheControl = "no-cache";
    },
};
app.UseDefaultFiles();
app.UseStaticFiles(dashboardFiles);
app.MapControllers();
// Client-side routes get the app; unknown API paths and missing files stay 404s.
app.MapFallbackToFile("{**path:nonfile:regex(^(?!api(/|$)|openapi\\.json$|health$))}", "index.html", dashboardFiles);
await app.RunAsync();
return 0;

// For WebApplicationFactory in the tests.
public partial class Program;
