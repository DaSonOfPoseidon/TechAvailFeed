using DbUp;

namespace TechAvail.Data;

// Applies the SQL migrations in Migrations/ (embedded, run in name order, each once; DbUp
// records them in schemaversions).
public static class Schema
{
    public static void Migrate(string connectionString)
    {
        var result = DeployChanges
            .To.PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(typeof(Schema).Assembly)
            .WithTransactionPerScript()
            .LogToNowhere()
            .Build()
            .PerformUpgrade();
        if (!result.Successful)
            throw new InvalidOperationException($"migration {result.ErrorScript?.Name} failed", result.Error);
    }
}
