using Npgsql;
using TechAvail.Data;

namespace TechAvail.Api;

// Listens on FeedStore.ChangedChannel and keeps the served snapshot in memory, so requests don't
// ask Postgres what's current. Generation counts the changes seen: cache keys built from it go
// stale together. Current is null while the listener is down, and readers then go to Postgres.
public sealed class FeedChanges(FeedStore store, ApiSettings settings, TimeProvider clock, ILogger<FeedChanges> log)
    : BackgroundService
{
    public sealed record State(long Generation, SnapshotMeta? Served);

    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    volatile State? current;
    long generation;

    public State? Current => current;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Keepalives notice a dead connection, which would otherwise wait for a notification forever.
        var connectionString = new NpgsqlConnectionStringBuilder(settings.ConnectionString) { KeepAlive = 30 }.ConnectionString;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(stoppingToken);
                await using (var listen = new NpgsqlCommand($"LISTEN {FeedStore.ChangedChannel}", connection))
                    await listen.ExecuteNonQueryAsync(stoppingToken);
                // Listening before the first read, so a change in between isn't missed.
                Refresh();
                while (true)
                {
                    await connection.WaitAsync(stoppingToken);
                    Refresh();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exc)
            {
                current = null;
                log.LogWarning(exc, "feed change listener down; reading through to Postgres");
                await Task.Delay(RetryDelay, clock, stoppingToken);
            }
        }
        current = null;
    }

    void Refresh() => current = new State(Interlocked.Increment(ref generation), store.SnapshotMeta());
}
