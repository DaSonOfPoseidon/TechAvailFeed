using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TechAvail.Core;
using TechAvail.Data;

namespace TechAvail.Ingest;

// The ingest service's own JSON endpoints. /health is open; the rest need
// API_KEY when one is set.
public static class Endpoints
{
    // snake_case and indented, since people read these endpoints directly.
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static IResult Send(int status, object body) => Results.Json(body, Json, statusCode: status);

    static IResult Error(int status, string error) => Send(status, new Dictionary<string, string> { ["error"] = error });

    public static void Map(WebApplication app)
    {
        app.Use(
            async (context, next) =>
            {
                var settings = context.RequestServices.GetRequiredService<IngestSettings>();
                var sent = Encoding.UTF8.GetBytes(context.Request.Headers["X-API-Key"].ToString());
                if (
                    context.Request.Path != "/health"
                    && settings.ApiKey.Length > 0
                    && !CryptographicOperations.FixedTimeEquals(sent, Encoding.UTF8.GetBytes(settings.ApiKey))
                )
                {
                    await Error(401, "missing or wrong X-API-Key").ExecuteAsync(context);
                    return;
                }
                try
                {
                    await next(context);
                }
                catch (Exception exc)
                {
                    context.RequestServices.GetRequiredService<ILogger<PollState>>().LogError(exc, "request failed");
                    await Error(500, exc.Message).ExecuteAsync(context);
                }
            }
        );

        // Stays 200 before the first delivery; the poll fields say what is wrong.
        app.MapGet(
            "/health",
            (IngestSettings settings, PollState state) =>
            {
                var (at, error) = state.Get();
                return Send(
                    200,
                    new Dictionary<string, object?>
                    {
                        ["ok"] = true,
                        ["mail_configured"] = settings.MailConfigured,
                        ["last_poll_at"] = at,
                        ["last_poll_error"] = error,
                    }
                );
            }
        );

        app.MapGet(
            "/latest.json",
            (FeedStore store, IngestSettings settings, TimeProvider clock) =>
            {
                var latest = store.Latest();
                if (latest is null)
                    return Error(404, "no snapshot yet");
                if (latest.Blocks is null)
                    return Send(200, new Dictionary<string, object?> { ["snapshot"] = latest.Snapshot, ["slots"] = latest.Slots });
                // Recomputed per request, so today's free time stays clipped to the real "now".
                var now = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), settings.Tz).DateTime;
                return Send(
                    200,
                    new Dictionary<string, object?>
                    {
                        ["snapshot"] = latest.Snapshot,
                        // A debug endpoint, but still served: never with exact home coordinates.
                        ["blocks"] = latest.Blocks.Select(b => Coords.PublicBlock(b)).ToList(),
                        ["free"] = Availability.FreeSlots(latest.Blocks, now),
                        ["unassigned_demand"] = Availability.Unassigned(latest.Blocks, DateOnly.FromDateTime(now)),
                    }
                );
            }
        );

        app.MapGet(
            "/history.json",
            (FeedStore store, IngestSettings settings, TimeProvider clock) =>
                Send(200, new OutcomeHistory(store, settings.Tz, clock).History())
        );

        app.MapGet("/runs.json", (FeedStore store) => Send(200, new { Summary = store.Latency(), Runs = store.Runs() }));

        app.MapFallback(() => Error(404, "not found"));
    }
}
