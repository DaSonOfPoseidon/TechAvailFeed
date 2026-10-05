using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace TechAvail.Parity;

// The .NET side of tools/api_golden.py: the same requests, at the same clock, against the same
// database, recorded in the same shape.
public static class ApiDump
{
    // Any type from the API assembly locates its entry point; this tool has its own Program.
    sealed class Factory(string connectionString, DateTimeOffset now) : WebApplicationFactory<TechAvail.Api.ApiSettings>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("DATABASE_URL", connectionString);
            builder.UseSetting("API_KEY", "");
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
            builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new FakeTimeProvider(now))));
        }
    }

    public static async Task<int> Run(string connectionString, string pythonFile, string output)
    {
        var python = JsonNode.Parse(File.ReadAllText(pythonFile))!;
        var now = DateTimeOffset.Parse(python["now"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        // The factory looks for the API's project folder; the API needs no files from it.
        Environment.SetEnvironmentVariable("ASPNETCORE_TEST_CONTENTROOT_TECHAVAIL_API", AppContext.BaseDirectory);
        using var factory = new Factory(connectionString, now);
        var client = factory.CreateClient();
        var responses = new JsonObject();
        foreach (var (path, expected) in python["responses"]!.AsObject())
        {
            var response = await client.GetAsync(path);
            if (path.StartsWith("/api/v1/export.xlsx", StringComparison.Ordinal))
            {
                var name = $"{Path.GetFileNameWithoutExtension(output)}_export{responses.Count}.xlsx";
                File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(output)!, name), await response.Content.ReadAsByteArrayAsync());
                var disposition = response.Content.Headers.TryGetValues("Content-Disposition", out var values) ? values.First() : null;
                responses[path] = new JsonObject { ["status"] = (int)response.StatusCode, ["body"] = disposition };
                continue;
            }
            var text = await response.Content.ReadAsStringAsync();
            var body = text.Length > 0 ? JsonNode.Parse(text) : null;
            // Like the Python side: only the status of framework validation errors, whose wording
            // differs between FastAPI and ASP.NET Core.
            if ((int)response.StatusCode == 422 && expected!["body"] is null)
                body = null;
            responses[path] = new JsonObject { ["status"] = (int)response.StatusCode, ["body"] = body };
        }
        File.WriteAllText(output, new JsonObject { ["now"] = python["now"]!.GetValue<string>(), ["responses"] = responses }.ToJsonString());
        Console.WriteLine($"{responses.Count} responses");
        return 0;
    }
}
