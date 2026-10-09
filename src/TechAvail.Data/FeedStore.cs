using Npgsql;
using NpgsqlTypes;
using TechAvail.Core.Parsing;

namespace TechAvail.Data;

// The write side of the store: snapshots, legacy slots and the diff-only blocks table.
public sealed partial class FeedStore(string connectionString)
{
    // The blocks content columns, in table (and BlockKey) order.
    public const string BlockColumns =
        "kind, work_date, tech_id, tech_name, starts_at, ends_at, ref_id, status, department, region, "
        + "skills, task_type, modified_at, modified_by, enroute_at, inprogress_at, prereqs_status, "
        + "address_issue, latitude, longitude, gps_precision";

    // The blocks rows of one snapshot, its id passed as @snapshot.
    public const string AtSnapshot =
        "first_snapshot_id <= @snapshot AND (closed_snapshot_id IS NULL OR closed_snapshot_id > @snapshot)";

    // Announced (NOTIFY) when a snapshot or finalized day commits, so readers know their cache is stale.
    public const string ChangedChannel = "feed_changed";

    // Delivered only if the transaction commits.
    static void NotifyChanged(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        using var command = new NpgsqlCommand($"NOTIFY {ChangedChannel}", connection, transaction);
        command.ExecuteNonQuery();
    }

    public NpgsqlConnection Open()
    {
        var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        return connection;
    }

    public bool Seen(string messageId)
    {
        using var connection = Open();
        using var command = new NpgsqlCommand("SELECT 1 FROM snapshots WHERE message_id = @id", connection);
        command.Parameters.AddWithValue("id", messageId);
        return command.ExecuteScalar() is not null;
    }

    public long Save(
        string messageId,
        string channel,
        string? subject,
        string? filename,
        DateTimeOffset? emailDate,
        DateTimeOffset? mailboxReceivedAt,
        ParsedFeed? feed,
        string? sha256 = null,
        string? error = null
    )
    {
        var status = error is not null ? "error" : feed is { RowCount: > 0 } ? "ok" : "empty";
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        long snapshotId;
        using (
            var command = new NpgsqlCommand(
                """
                INSERT INTO snapshots (
                    message_id, channel, subject, filename, generated_at, email_date,
                    mailbox_received_at, row_count, sha256, status, error, format
                )
                VALUES (@message_id, @channel, @subject, @filename, @generated_at, @email_date,
                        @mailbox_received_at, @row_count, @sha256, @status, @error, @format)
                RETURNING id
                """,
                connection,
                transaction
            )
        )
        {
            command.Parameters.AddWithValue("message_id", messageId);
            command.Parameters.AddWithValue("channel", channel);
            command.Parameters.AddWithValue("subject", (object?)subject ?? DBNull.Value);
            command.Parameters.AddWithValue("filename", (object?)filename ?? DBNull.Value);
            command.Parameters.AddWithValue("generated_at", Utc(feed?.GeneratedAt));
            command.Parameters.AddWithValue("email_date", Utc(emailDate));
            command.Parameters.AddWithValue("mailbox_received_at", Utc(mailboxReceivedAt));
            command.Parameters.AddWithValue("row_count", feed?.RowCount ?? 0);
            command.Parameters.AddWithValue("sha256", (object?)(feed?.Sha256 ?? sha256) ?? DBNull.Value);
            command.Parameters.AddWithValue("status", status);
            command.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
            command.Parameters.AddWithValue("format", feed?.Format ?? "slots");
            snapshotId = (long)command.ExecuteScalar()!;
        }
        if (status == "ok" && feed!.Format == "blocks")
            SaveBlocks(connection, transaction, snapshotId, feed.Blocks);
        else if (status == "ok")
            SaveSlots(connection, snapshotId, feed!.Slots);
        NotifyChanged(connection, transaction);
        transaction.Commit();
        return snapshotId;
    }

    // timestamptz takes UTC values only; the instant is what is stored either way.
    static object Utc(DateTimeOffset? value) => value is { } v ? v.ToUniversalTime() : DBNull.Value;

    static void SaveSlots(NpgsqlConnection connection, long snapshotId, List<Slot> slots)
    {
        using var copy = connection.BeginBinaryImport(
            "COPY slots (snapshot_id, work_date, tech_id, tech_name, open_from, open_until, open_minutes, "
                + "region, skills) FROM STDIN (FORMAT BINARY)"
        );
        foreach (var s in slots)
        {
            copy.StartRow();
            copy.Write(snapshotId, NpgsqlDbType.Bigint);
            copy.Write(s.WorkDate, NpgsqlDbType.Date);
            copy.Write(s.TechId, NpgsqlDbType.Text);
            copy.Write(s.TechName, NpgsqlDbType.Text);
            copy.Write(s.OpenFrom, NpgsqlDbType.Timestamp);
            copy.Write(s.OpenUntil, NpgsqlDbType.Timestamp);
            copy.Write(s.OpenMinutes, NpgsqlDbType.Integer);
            copy.Write(s.Region, NpgsqlDbType.Text);
            copy.Write(s.Skills, NpgsqlDbType.Text);
        }
        copy.Complete();
    }

    // Only what changed since the previous blocks snapshot is written. The current rows are read
    // raw (NULLs kept) so a reconstructed snapshot matches what was ingested.
    static void SaveBlocks(NpgsqlConnection connection, NpgsqlTransaction transaction, long snapshotId, List<Block> blocks)
    {
        var current = new List<(long, BlockKey)>();
        using (
            var command = new NpgsqlCommand(
                $"SELECT id, {BlockColumns} FROM blocks WHERE closed_snapshot_id IS NULL",
                connection,
                transaction
            )
        )
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                current.Add((reader.GetInt64(0), ReadKey(reader, 1)));

        var (insert, close) = BlockDiff.Diff(current, blocks.Select(BlockKey.From).ToList());
        if (close.Count > 0)
        {
            using var command = new NpgsqlCommand(
                "UPDATE blocks SET closed_snapshot_id = @snapshot WHERE id = ANY(@ids)",
                connection,
                transaction
            );
            command.Parameters.AddWithValue("snapshot", snapshotId);
            command.Parameters.AddWithValue("ids", close.ToArray());
            command.ExecuteNonQuery();
        }
        if (insert.Count == 0)
            return;
        // COPY keeps row order, so ids are assigned in snapshot order.
        using var copy = connection.BeginBinaryImport(
            $"COPY blocks (first_snapshot_id, {BlockColumns}) FROM STDIN (FORMAT BINARY)"
        );
        foreach (var b in insert)
        {
            copy.StartRow();
            copy.Write(snapshotId, NpgsqlDbType.Bigint);
            copy.Write(b.Kind, NpgsqlDbType.Text);
            copy.Write(b.WorkDate, NpgsqlDbType.Date);
            copy.Write(b.TechId, NpgsqlDbType.Text);
            copy.Write(b.TechName, NpgsqlDbType.Text);
            copy.Write(b.StartsAt, NpgsqlDbType.Timestamp);
            copy.Write(b.EndsAt, NpgsqlDbType.Timestamp);
            WriteText(copy, b.RefId);
            WriteText(copy, b.Status);
            WriteText(copy, b.Department);
            WriteText(copy, b.Region);
            WriteText(copy, b.Skills);
            WriteText(copy, b.TaskType);
            WriteTimestamp(copy, b.ModifiedAt);
            WriteText(copy, b.ModifiedBy);
            WriteTimestamp(copy, b.EnrouteAt);
            WriteTimestamp(copy, b.InprogressAt);
            WriteText(copy, b.PrereqsStatus);
            WriteText(copy, b.AddressIssue);
            WriteDouble(copy, b.Latitude);
            WriteDouble(copy, b.Longitude);
            WriteText(copy, b.GpsPrecision);
        }
        copy.Complete();
    }

    static void WriteText(NpgsqlBinaryImporter copy, string? value)
    {
        if (value is null)
            copy.WriteNull();
        else
            copy.Write(value, NpgsqlDbType.Text);
    }

    static void WriteTimestamp(NpgsqlBinaryImporter copy, DateTime? value)
    {
        if (value is null)
            copy.WriteNull();
        else
            copy.Write(value.Value, NpgsqlDbType.Timestamp);
    }

    static void WriteDouble(NpgsqlBinaryImporter copy, double? value)
    {
        if (value is null)
            copy.WriteNull();
        else
            copy.Write(value.Value, NpgsqlDbType.Double);
    }

    // The BlockColumns starting at ordinal `first`, NULLs kept.
    public static BlockKey ReadKey(NpgsqlDataReader r, int first)
    {
        string? Text(int i) => r.IsDBNull(first + i) ? null : r.GetString(first + i);
        DateTime? Time(int i) => r.IsDBNull(first + i) ? null : r.GetDateTime(first + i);
        double? Number(int i) => r.IsDBNull(first + i) ? null : r.GetDouble(first + i);
        return new BlockKey(
            r.GetString(first),
            r.GetFieldValue<DateOnly>(first + 1),
            r.GetString(first + 2),
            r.GetString(first + 3),
            r.GetDateTime(first + 4),
            r.GetDateTime(first + 5),
            Text(6),
            Text(7),
            Text(8),
            Text(9),
            Text(10),
            Text(11),
            Time(12),
            Text(13),
            Time(14),
            Time(15),
            Text(16),
            Text(17),
            Number(18),
            Number(19),
            Text(20)
        );
    }
}
