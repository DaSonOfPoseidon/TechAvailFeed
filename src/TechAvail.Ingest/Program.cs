using TechAvail.Data;
using TechAvail.Ingest;

// Ingest service: polls the mailbox for the scheduled feed, writes snapshots to Postgres and serves
// its own status endpoints.
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
builder.Services.AddSingleton(services => IngestSettings.From(services.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(services => new FeedStore(services.GetRequiredService<IngestSettings>().ConnectionString));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PollState>();
builder.Services.AddSingleton<IMailbox, ImapMailbox>();
builder.Services.AddSingleton<Poller>();
builder.Services.AddHostedService<PollWorker>();

var app = builder.Build();
Schema.Migrate(app.Services.GetRequiredService<IngestSettings>().ConnectionString);
Endpoints.Map(app);
await app.RunAsync();
return 0;

// For WebApplicationFactory in the tests.
public partial class Program;
