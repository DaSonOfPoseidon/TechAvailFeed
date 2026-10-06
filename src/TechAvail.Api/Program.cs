using System.Text.Json;
using TechAvail.Api;
using TechAvail.Api.Controllers;
using TechAvail.Data;

// Dashboard API: JSON for the calendar and KPI charts, and the Excel export. Read-only; the
// ingest service writes everything. Port of api/main.py.
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
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddCors();
builder.Services
    .AddControllers(options => options.Filters.Add<ApiExceptionFilter>())
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        options.JsonSerializerOptions.Converters.Add(new UtcZConverter());
    });
// Bad query values are a 422, as in FastAPI (ASP.NET Core would answer 400).
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
app.MapControllers();
await app.RunAsync();
return 0;

// For WebApplicationFactory in the tests.
public partial class Program;
