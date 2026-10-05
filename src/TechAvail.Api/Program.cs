using System.Text.Json;
using TechAvail.Api;
using TechAvail.Api.Controllers;
using TechAvail.Data;

// Dashboard API: JSON for the calendar and KPI charts, and the Excel export. Read-only; the
// ingest service writes everything. Port of api/main.py.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(services => ApiSettings.From(services.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(services => new FeedStore(services.GetRequiredService<ApiSettings>().ConnectionString));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddCors();
builder.Services
    .AddControllers(options => options.Filters.Add<ApiExceptionFilter>())
    .AddJsonOptions(options => options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
builder.Services.AddOpenApi();

var app = builder.Build();
var settings = app.Services.GetRequiredService<ApiSettings>();
if (settings.CorsOrigins.Length > 0)
    app.UseCors(cors =>
        cors.WithOrigins(settings.CorsOrigins).WithMethods("GET").WithHeaders("X-API-Key").WithExposedHeaders("Content-Disposition")
    );
app.MapOpenApi("/openapi.json");
app.MapControllers();
app.Run();

// For WebApplicationFactory in the tests.
public partial class Program;
