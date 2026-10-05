using TechAvail.Data;

namespace TechAvail.Api;

// The same environment the Python API reads (api/main.py create_app).
public sealed record ApiSettings(
    string ConnectionString,
    TimeZoneInfo Tz,
    string ApiKey,
    string[] CorsOrigins,
    bool ExactCoords
)
{
    public static ApiSettings From(IConfiguration config) =>
        new(
            ConnectionStrings.FromUrl(config["DATABASE_URL"] ?? throw new InvalidOperationException("DATABASE_URL is not set")),
            TimeZoneInfo.FindSystemTimeZoneById(config["MAIL_TZ"] ?? "America/Chicago"),
            config["API_KEY"] ?? "",
            [.. (config["CORS_ORIGINS"] ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)],
            // Off by default: exact coordinates locate a customer's home (Core/Coords.cs).
            (config["EXACT_COORDS"] ?? "").Trim().ToLowerInvariant() is "1" or "true" or "yes"
        );
}
