using Dapper;

namespace TechAvail.Data.Tests;

public class SchemaTests
{
    [DbFact]
    public void Baseline_creates_the_python_schema()
    {
        using var db = new TestDatabase();
        using var connection = db.Open();
        var tables = connection.Query<string>(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' ORDER BY 1"
        );
        Assert.Equal(
            ["blocks", "current_blocks", "job_outcomes", "outcome_days", "schemaversions", "slots", "snapshots"],
            tables
        );
    }

    [DbFact]
    public void Migrating_twice_is_a_no_op()
    {
        using var db = new TestDatabase();
        Schema.Migrate(db.ConnectionString);
        using var connection = db.Open();
        Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM schemaversions"));
    }

    [DbFact]
    public void Baseline_runs_on_a_database_python_already_set_up()
    {
        // The live database was created by store.py, without DbUp's journal: the baseline must apply
        // cleanly on top of it.
        using var db = new TestDatabase(migrate: false);
        var baseline = new StreamReader(
            typeof(Schema).Assembly.GetManifestResourceStream("TechAvail.Data.Migrations.0001_baseline.sql")!
        ).ReadToEnd();
        using (var connection = db.Open())
            connection.Execute(baseline);
        Schema.Migrate(db.ConnectionString);
    }
}
