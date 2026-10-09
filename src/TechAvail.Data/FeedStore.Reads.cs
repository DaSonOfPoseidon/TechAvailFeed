using Npgsql;
using TechAvail.Core;
using TechAvail.Core.Parsing;

namespace TechAvail.Data;

public sealed record SnapshotRow(
    long Id,
    string MessageId,
    string Channel,
    string? Subject,
    string? Filename,
    DateTimeOffset? GeneratedAt,
    DateTimeOffset? EmailDate,
    DateTimeOffset? MailboxReceivedAt,
    DateTimeOffset IngestedAt,
    int RowCount,
    string? Sha256,
    string Status,
    string? Error,
    string Format
);

public sealed record LatestSnapshot(SnapshotRow Snapshot, List<Block>? Blocks, List<Slot>? Slots);

public sealed record SnapshotMeta(long Id, string Format, DateTimeOffset? GeneratedAt, DateTimeOffset IngestedAt);

public sealed record RunRow(
    long Id,
    string Status,
    string Format,
    int RowCount,
    DateTimeOffset? GeneratedAt,
    DateTimeOffset? MailboxReceivedAt,
    DateTimeOffset IngestedAt,
    decimal? MbsToMailboxMin,
    decimal? MailboxToIngestMin,
    decimal? TotalMin,
    string? Error
);

public sealed record LatencySummary(
    long Runs,
    long Ok,
    long Empty,
    long Errors,
    double? TotalMedianMin,
    double? TotalP95Min,
    decimal? TotalMaxMin
);

// The read side of the store.
public sealed partial class FeedStore
{
    // Every Block field, with NULLs from older snapshots read as the parser's defaults.
    const string BlockFields = """
        kind, work_date, tech_id, tech_name, starts_at, ends_at, ref_id, status,
        COALESCE(department, '') AS department, COALESCE(region, '') AS region,
        COALESCE(skills, '') AS skills, COALESCE(task_type, '') AS task_type, modified_at,
        COALESCE(modified_by, '') AS modified_by, enroute_at, inprogress_at,
        COALESCE(prereqs_status, '') AS prereqs_status, address_issue, latitude, longitude,
        COALESCE(gps_precision, '') AS gps_precision
        """;

    // Delivery latency per snapshot, split at the mailbox: source -> mailbox, then mailbox -> poller.
    const string RunsSql = """
        SELECT
            id, status, format, row_count, generated_at, mailbox_received_at, ingested_at,
            EXTRACT(EPOCH FROM (mailbox_received_at - generated_at)) / 60 AS mbs_to_mailbox_min,
            EXTRACT(EPOCH FROM (ingested_at - mailbox_received_at)) / 60 AS mailbox_to_ingest_min,
            EXTRACT(EPOCH FROM (ingested_at - generated_at)) / 60 AS total_min,
            error
        FROM snapshots
        ORDER BY id DESC
        LIMIT @limit
        """;

    const string LatencySql = """
        SELECT
            COUNT(*) AS runs,
            COUNT(*) FILTER (WHERE status = 'ok') AS ok,
            COUNT(*) FILTER (WHERE status = 'empty') AS empty,
            COUNT(*) FILTER (WHERE status = 'error') AS errors,
            percentile_cont(0.5) WITHIN GROUP (ORDER BY total_min) AS total_median_min,
            percentile_cont(0.95) WITHIN GROUP (ORDER BY total_min) AS total_p95_min,
            MAX(total_min) AS total_max_min
        FROM (
            SELECT status, EXTRACT(EPOCH FROM (ingested_at - generated_at)) / 60 AS total_min
            FROM snapshots
        ) s
        """;

    // The snapshot the dashboard serves: an empty or failed run never replaces the last good
    // one, and while both feeds run the newest blocks snapshot wins over a newer legacy one.
    const string ServedSnapshot = "WHERE status = 'ok' ORDER BY (format = 'blocks') DESC, id DESC LIMIT 1";

    NpgsqlCommand Command(NpgsqlConnection connection, string sql, params (string, object?)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    List<T> Query<T>(string sql, Func<NpgsqlDataReader, T> read, params (string, object?)[] parameters)
    {
        using var connection = Open();
        using var command = Command(connection, sql, parameters);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
            rows.Add(read(reader));
        return rows;
    }

    static T? Get<T>(NpgsqlDataReader r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? default : r.GetFieldValue<T>(i);
    }

    static DateTimeOffset? Instant(NpgsqlDataReader r, string name) =>
        Get<DateTime?>(r, name) is { } utc ? new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)) : null;

    static SnapshotRow ReadSnapshot(NpgsqlDataReader r) =>
        new(
            Get<long>(r, "id"),
            Get<string>(r, "message_id")!,
            Get<string>(r, "channel")!,
            Get<string>(r, "subject"),
            Get<string>(r, "filename"),
            Instant(r, "generated_at"),
            Instant(r, "email_date"),
            Instant(r, "mailbox_received_at"),
            Instant(r, "ingested_at")!.Value,
            Get<int>(r, "row_count"),
            Get<string>(r, "sha256"),
            Get<string>(r, "status")!,
            Get<string>(r, "error"),
            Get<string>(r, "format")!
        );

    public static Block ReadBlock(NpgsqlDataReader r) =>
        new()
        {
            Kind = Get<string>(r, "kind")!,
            WorkDate = Get<DateOnly>(r, "work_date"),
            TechId = Get<string>(r, "tech_id")!,
            TechName = Get<string>(r, "tech_name")!,
            StartsAt = Get<DateTime>(r, "starts_at"),
            EndsAt = Get<DateTime>(r, "ends_at"),
            // ref_id and status are never NULL in rows the ingest wrote.
            RefId = Get<string>(r, "ref_id")!,
            Status = Get<string>(r, "status")!,
            Department = Get<string>(r, "department")!,
            Region = Get<string>(r, "region")!,
            Skills = Get<string>(r, "skills")!,
            TaskType = Get<string>(r, "task_type")!,
            ModifiedAt = Get<DateTime?>(r, "modified_at"),
            ModifiedBy = Get<string>(r, "modified_by")!,
            EnrouteAt = Get<DateTime?>(r, "enroute_at"),
            InprogressAt = Get<DateTime?>(r, "inprogress_at"),
            PrereqsStatus = Get<string>(r, "prereqs_status")!,
            AddressIssue = Get<string>(r, "address_issue"),
            Latitude = Get<double?>(r, "latitude"),
            Longitude = Get<double?>(r, "longitude"),
            GpsPrecision = Get<string>(r, "gps_precision")!,
        };

    public LatestSnapshot? Latest()
    {
        var snapshot = Query($"SELECT * FROM snapshots {ServedSnapshot}", ReadSnapshot).FirstOrDefault();
        if (snapshot is null)
            return null;
        if (snapshot.Format == "blocks")
        {
            var blocks = Query(
                $"SELECT {BlockFields} FROM blocks WHERE {AtSnapshot} "
                    + "ORDER BY work_date, tech_name, starts_at, kind, ref_id, id",
                ReadBlock,
                ("snapshot", snapshot.Id)
            );
            return new LatestSnapshot(snapshot, blocks, null);
        }
        var slots = Query(
            """
            SELECT work_date, tech_id, tech_name, open_from, open_until, open_minutes, region, skills
            FROM slots WHERE snapshot_id = @snapshot ORDER BY work_date, tech_name, open_from
            """,
            r => new Slot(
                Get<DateOnly>(r, "work_date"),
                Get<string>(r, "tech_id")!,
                Get<string>(r, "tech_name")!,
                Get<DateTime>(r, "open_from"),
                Get<DateTime>(r, "open_until"),
                Get<int>(r, "open_minutes"),
                Get<string>(r, "region")!,
                Get<string>(r, "skills")!
            ),
            ("snapshot", snapshot.Id)
        );
        return new LatestSnapshot(snapshot, null, slots);
    }

    public List<RunRow> Runs(int limit = 200) =>
        Query(
            RunsSql,
            r => new RunRow(
                Get<long>(r, "id"),
                Get<string>(r, "status")!,
                Get<string>(r, "format")!,
                Get<int>(r, "row_count"),
                Instant(r, "generated_at"),
                Instant(r, "mailbox_received_at"),
                Instant(r, "ingested_at")!.Value,
                Get<decimal?>(r, "mbs_to_mailbox_min"),
                Get<decimal?>(r, "mailbox_to_ingest_min"),
                Get<decimal?>(r, "total_min"),
                Get<string>(r, "error")
            ),
            ("limit", limit)
        );

    public LatencySummary Latency() =>
        Query(
            LatencySql,
            r => new LatencySummary(
                Get<long>(r, "runs"),
                Get<long>(r, "ok"),
                Get<long>(r, "empty"),
                Get<long>(r, "errors"),
                Get<double?>(r, "total_median_min"),
                Get<double?>(r, "total_p95_min"),
                Get<decimal?>(r, "total_max_min")
            )
        )[0];

    public List<(long Id, DateTimeOffset GeneratedAt)> BlocksSnapshots() =>
        Query(
            """
            SELECT id, generated_at FROM snapshots
            WHERE status = 'ok' AND format = 'blocks' AND generated_at IS NOT NULL
            ORDER BY generated_at
            """,
            r => (Get<long>(r, "id"), Instant(r, "generated_at")!.Value)
        );

    // Only the rows the outcome history needs: jobs and tickets, on or off the calendar.
    public List<Block> WorkBlocks(long snapshotId) =>
        Query(
            $"SELECT {BlockFields} FROM blocks WHERE {AtSnapshot} AND kind NOT IN ('shift', 'shift_tc', 'time_off')",
            ReadBlock,
            ("snapshot", snapshotId)
        );

    // Each tech's shift region on one day: fallback for work from before the query sent its own.
    public Dictionary<string, string> ShiftRegions(long snapshotId, DateOnly day) =>
        Query(
            $"""
            SELECT DISTINCT ON (tech_id) tech_id, COALESCE(region, '') AS region
            FROM blocks
            WHERE {AtSnapshot} AND kind IN ('shift', 'shift_tc') AND work_date = @day
            ORDER BY tech_id, kind <> 'shift', starts_at
            """,
            r => (Get<string>(r, "tech_id")!, Get<string>(r, "region")!),
            ("snapshot", snapshotId),
            ("day", day)
        ).ToDictionary(row => row.Item1, row => row.Item2);

    // The snapshot Latest() would serve, without loading its rows.
    public SnapshotMeta? SnapshotMeta() =>
        Query(
            $"SELECT id, format, generated_at, ingested_at FROM snapshots {ServedSnapshot}",
            r => new SnapshotMeta(
                Get<long>(r, "id"),
                Get<string>(r, "format")!,
                Instant(r, "generated_at"),
                Instant(r, "ingested_at")!.Value
            )
        ).FirstOrDefault();

    public HashSet<DateOnly> FinalizedDays() =>
        [.. Query("SELECT plan_date FROM outcome_days", r => Get<DateOnly>(r, "plan_date"))];

    public void SaveDay(DayOutcome outcome)
    {
        var morning = outcome.Morning;
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (
            var command = Command(
                connection,
                """
                INSERT INTO outcome_days (plan_date, status, morning_snapshot_id, morning_at, added_job, added_ticket)
                VALUES (@day, @status, @morning_id, @morning_at, @added_job, @added_ticket)
                ON CONFLICT (plan_date) DO NOTHING
                """,
                ("day", outcome.Day),
                ("status", outcome.Status),
                ("morning_id", morning?.Id),
                ("morning_at", morning?.At),
                ("added_job", outcome.AddedAfterMorning.GetValueOrDefault("job")),
                ("added_ticket", outcome.AddedAfterMorning.GetValueOrDefault("ticket"))
            )
        )
        {
            command.Transaction = transaction;
            command.ExecuteNonQuery();
        }
        foreach (var item in outcome.Items)
        {
            using var command = Command(
                connection,
                """
                INSERT INTO job_outcomes (
                    plan_date, kind, ref_id, tech_id, tech_name, planned_start, morning_snapshot_id,
                    d0, d1, d2, reached, prereqs_open, region, task_type
                )
                VALUES (@day, @kind, @ref_id, @tech_id, @tech_name, @planned_start, @morning_id,
                        @d0, @d1, @d2, @reached, @prereqs_open, @region, @task_type)
                ON CONFLICT DO NOTHING
                """,
                ("day", outcome.Day),
                ("kind", item.Kind),
                ("ref_id", item.RefId),
                ("tech_id", item.TechId),
                ("tech_name", item.TechName),
                ("planned_start", item.PlannedStart),
                ("morning_id", morning!.Id),
                ("d0", item.Outcomes["d0"]),
                ("d1", item.Outcomes["d1"]),
                ("d2", item.Outcomes["d2"]),
                ("reached", item.Reached),
                ("prereqs_open", item.PrereqsOpen),
                ("region", item.Region),
                ("task_type", item.TaskType)
            );
            command.Transaction = transaction;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public DayOutcome? LoadDay(DateOnly day)
    {
        var row = Query(
            "SELECT * FROM outcome_days WHERE plan_date = @day",
            r =>
                (
                    Status: Get<string>(r, "status")!,
                    MorningId: Get<long?>(r, "morning_snapshot_id"),
                    MorningAt: Get<DateTime?>(r, "morning_at"),
                    Job: Get<int>(r, "added_job"),
                    Ticket: Get<int>(r, "added_ticket")
                ),
            ("day", day)
        );
        if (row.Count == 0)
            return null;
        var items = Query(
            """
            SELECT kind, ref_id, tech_id, tech_name, planned_start, d0, d1, d2,
                   COALESCE(reached, 'unknown') AS reached, prereqs_open,
                   COALESCE(region, '') AS region, COALESCE(task_type, '') AS task_type
            FROM job_outcomes WHERE plan_date = @day
            ORDER BY kind, planned_start, ref_id
            """,
            r =>
                new Planned(
                    Get<string>(r, "kind")!,
                    Get<string>(r, "ref_id")!,
                    Get<string>(r, "tech_id")!,
                    Get<string>(r, "tech_name")!,
                    Get<DateTime>(r, "planned_start")
                )
                {
                    Outcomes = new()
                    {
                        ["d0"] = Get<string>(r, "d0")!,
                        ["d1"] = Get<string>(r, "d1")!,
                        ["d2"] = Get<string>(r, "d2")!,
                    },
                    Reached = Get<string>(r, "reached")!,
                    PrereqsOpen = Get<bool?>(r, "prereqs_open"),
                    Region = Get<string>(r, "region")!,
                    TaskType = Get<string>(r, "task_type")!,
                },
            ("day", day)
        );
        var (status, morningId, morningAt, job, ticket) = row[0];
        var morning = morningId is { } id ? new Snapshot(id, morningAt!.Value) : null;
        return new DayOutcome(day, status, morning, items, new() { ["job"] = job, ["ticket"] = ticket });
    }
}
