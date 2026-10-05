using TechAvail.Data;

namespace TechAvail.Ingest;

// The last poll's time and error, for /health.
public sealed class PollState
{
    readonly Lock gate = new();
    DateTimeOffset? lastPollAt;
    string? lastPollError;

    public void Record(DateTimeOffset at, string? error)
    {
        lock (gate)
            (lastPollAt, lastPollError) = (at, error);
    }

    public (DateTimeOffset? At, string? Error) Get()
    {
        lock (gate)
            return (lastPollAt, lastPollError);
    }
}

// Port of poll_once in feed/__main__.py: fetch new feed mail, ingest each once, file it as processed
// or failed, purge processed mail from before today, then finalize the outcome history.
public sealed class Poller(FeedStore store, IMailbox mailbox, IngestSettings settings, TimeProvider clock, ILogger<Poller> log)
{
    public void PollOnce()
    {
        var ingested = false;
        using (var session = mailbox.Open())
        {
            var mails = session.FetchNew();
            session.EnsureLabels();
            foreach (var fetched in mails)
            {
                bool ok;
                if (store.Seen(fetched.Mail.MessageId))
                    ok = true;
                else
                {
                    ok = FeedIngest.Ingest(store, fetched.Mail, fetched.ReceivedAt);
                    ingested = true;
                    log.LogInformation("ingested {MessageId}: {Outcome}", fetched.Mail.MessageId, ok ? "processed" : "failed");
                }
                if (ok && settings.ArchiveDir.Length > 0)
                    MailArchive.Save(settings.ArchiveDir, fetched.Mail.MessageId, fetched.ReceivedAt, fetched.Raw);
                session.FileAway(fetched.Uid, ok ? settings.ProcessedLabel : settings.FailedLabel);
            }
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), settings.Tz).DateTime);
            var purged = session.PurgeProcessed(today);
            if (purged > 0)
                log.LogInformation("deleted {Count} processed emails from before {Today}", purged, today);
        }
        if (ingested)
        {
            var written = new OutcomeHistory(store, settings.Tz, clock).Finalize();
            if (written > 0)
                log.LogInformation("finalized outcome history for {Count} days", written);
        }
    }
}

// The poll loop. Errors are logged and recorded, never fatal: the next poll tries again.
public sealed class PollWorker(Poller poller, PollState state, IngestSettings settings, TimeProvider clock, ILogger<PollWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.MailConfigured)
        {
            log.LogWarning("IMAP_USER / IMAP_PASSWORD / MAIL_FROM not set; serving HTTP only");
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            string? error = null;
            try
            {
                await Task.Run(poller.PollOnce, stoppingToken);
            }
            catch (Exception exc) when (exc is not OperationCanceledException)
            {
                log.LogError(exc, "poll failed");
                error = exc.Message;
            }
            state.Record(clock.GetUtcNow(), error);
            await Task.Delay(TimeSpan.FromSeconds(settings.PollSeconds), stoppingToken);
        }
    }
}
