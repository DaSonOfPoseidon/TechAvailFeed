using Npgsql;

namespace TechAvail.Data.Tests;

public class ConnectionStringsTests
{
    [Fact]
    public void A_postgres_url_becomes_npgsql_pairs()
    {
        var parsed = new NpgsqlConnectionStringBuilder(
            ConnectionStrings.FromUrl("postgresql://techavail:p%40ss%3Aword@postgres:5433/techavail")
        );
        Assert.Equal(("postgres", 5433, "techavail", "techavail", "p@ss:word"), (parsed.Host, parsed.Port, parsed.Database, parsed.Username, parsed.Password));
    }

    [Fact]
    public void The_default_port_and_key_value_strings_are_kept() =>
        Assert.Equal(
            ("Host=h;Database=d", 5432),
            (ConnectionStrings.FromUrl("Host=h;Database=d"), new NpgsqlConnectionStringBuilder(ConnectionStrings.FromUrl("postgres://u@h/d")).Port)
        );
}
