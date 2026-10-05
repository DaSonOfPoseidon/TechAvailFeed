using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using TechAvail.Data.Tests;

namespace TechAvail.Api.Tests;

// The API over a fresh test database, with a fake clock and optional settings.
public sealed class ApiFactory(TestDatabase db, DateTimeOffset now, string apiKey = "") : WebApplicationFactory<Program>
{
    public FakeTimeProvider Clock { get; } = new(now);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("DATABASE_URL", db.ConnectionString);
        builder.UseSetting("API_KEY", apiKey);
        builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock)));
    }
}
