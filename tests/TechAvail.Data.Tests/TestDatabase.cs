using Npgsql;

namespace TechAvail.Data.Tests;

// A fresh, migrated database per test in the Postgres named by TEST_DATABASE_URL
// (scripts/test-db.sh locally, a service container in CI), dropped afterwards.
public sealed class TestDatabase : IDisposable
{
    public static readonly string? Server = Environment.GetEnvironmentVariable("TEST_DATABASE_URL");

    readonly string name = $"t_{Guid.NewGuid():N}";

    public string ConnectionString { get; }

    public TestDatabase(bool migrate = true)
    {
        if (Server is null)
            throw new InvalidOperationException("TEST_DATABASE_URL is not set");
        Admin($"CREATE DATABASE {name}");
        ConnectionString = new NpgsqlConnectionStringBuilder(Server) { Database = name }.ConnectionString;
        if (migrate)
            Schema.Migrate(ConnectionString);
    }

    public NpgsqlConnection Open()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    static void Admin(string sql)
    {
        using var connection = new NpgsqlConnection(Server);
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        NpgsqlConnection.ClearAllPools();
        Admin($"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
    }
}

// Skipped, not failed, when no test Postgres is configured.
public sealed class DbFactAttribute : FactAttribute
{
    public DbFactAttribute()
    {
        if (TestDatabase.Server is null)
            Skip = "TEST_DATABASE_URL is not set (scripts/test-db.sh up)";
    }
}
