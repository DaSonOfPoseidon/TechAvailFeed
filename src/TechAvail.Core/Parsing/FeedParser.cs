using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;

namespace TechAvail.Core.Parsing;

// Parses the scheduled feed CSV (docs/feed-format.md) into blocks, or slots for the legacy format.
// Anything malformed rejects the whole file with a FeedParseException naming the line.
public static partial class FeedParser
{
    // Legacy gap format, still emitted by the registered feed query until it is updated.
    // Headers are matched case-insensitively (the scheduling system upper-cases them).
    public static readonly string[] RequiredColumns =
    [
        "generated_at",
        "work_date",
        "tech_id",
        "tech_name",
        "open_from",
        "open_until",
        "open_minutes",
        "region",
        "skills",
    ];

    // Raw calendar blocks (docs/feed-format.md). A header with a "kind" column selects this format.
    public static readonly string[] BlockColumns =
    [
        "generated_at",
        "kind",
        "work_date",
        "tech_id",
        "tech_name",
        "starts_at",
        "ends_at",
        "ref_id",
        "status",
        "region",
        "skills",
    ];

    // Added after the first blocks release; a missing one reads as "".
    public static readonly string[] OptionalBlockColumns =
    [
        "department",
        "task_type",
        "modified_at",
        "modified_by",
        "enroute_at",
        "inprogress_at",
        "pre-reqs status",
        "address_issue",
        "latitude",
        "longitude",
        "gps_precision",
    ];

    public static readonly string[] BlockKinds =
    [
        "shift",
        "shift_tc",
        "job",
        "ticket",
        "time_off",
        "job_moved",
        "ticket_moved",
        "job_unassigned",
        "ticket_unassigned",
    ];

    static readonly string[] DateFormats = ["yyyy-M-d", "M-d-yyyy", "M/d/yyyy"];

    // The scheduling system rewrites a seconds-precision value as "09-29-2026 12:03:34".
    static readonly string[] TimestampFormats = ["yyyy-M-d H:mm", "yyyy-M-d H:mm:ss", "M-d-yyyy H:mm:ss", "M-d-yyyy h:mm tt"];

    const DateTimeStyles Lenient = DateTimeStyles.AllowWhiteSpaces;

    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static ParsedFeed Parse(byte[] data)
    {
        var parsed = new ParsedFeed { Sha256 = Convert.ToHexStringLower(SHA256.HashData(data)) };
        string text;
        try
        {
            text = StrictUtf8.GetString(data);
        }
        catch (DecoderFallbackException)
        {
            throw new FeedParseException("file is not valid UTF-8");
        }
        // utf-8-sig: one leading byte-order mark is dropped.
        if (text.StartsWith('﻿'))
            text = text[1..];

        using var csv = new TextFieldParser(new StringReader(text))
        {
            TextFieldType = FieldType.Delimited,
            Delimiters = [","],
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false,
        };
        var header = ReadRow(csv) ?? throw new FeedParseException("file is empty, no header row");
        var columns = header.Fields.Select(name => name.Trim().ToLowerInvariant()).ToList();
        if (columns.Contains("kind"))
            parsed.Format = "blocks";
        var required = parsed.Format == "blocks" ? BlockColumns : RequiredColumns;
        var missing = required.Where(name => !columns.Contains(name)).ToList();
        if (missing.Count > 0)
            throw new FeedParseException($"missing columns: {string.Join(", ", missing)}");
        var index = required.Concat(parsed.Format == "blocks" ? OptionalBlockColumns.Where(columns.Contains) : [])
            .ToDictionary(name => name, columns.IndexOf);

        // Blank lines are skipped by the reader.
        while (ReadRow(csv) is { } next)
        {
            var (line, row) = next;
            if (row.All(string.IsNullOrWhiteSpace))
                continue;
            if (row.Length != columns.Count)
                throw new FeedParseException($"line {line}: expected {columns.Count} fields, got {row.Length}");
            var values = index.ToDictionary(pair => pair.Key, pair => row[pair.Value]);
            parsed.GeneratedAt ??= ParseGeneratedAt(values["generated_at"]);
            if (parsed.Format == "blocks")
                parsed.Blocks.Add(ParseBlock(values, line));
            else
                parsed.Slots.Add(ParseSlot(values, line));
        }
        return parsed;
    }

    // The next record and the line it starts on, or null at the end of the file.
    static (long Line, string[] Fields)? ReadRow(TextFieldParser csv)
    {
        var line = csv.LineNumber;
        try
        {
            return csv.ReadFields() is { } fields ? (line, fields) : null;
        }
        catch (MalformedLineException exc)
        {
            throw new FeedParseException($"line {exc.LineNumber}: malformed CSV");
        }
    }

    public static DateOnly ParseDate(string value) =>
        DateOnly.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, Lenient, out var parsed)
            ? parsed
            : throw new FeedParseException($"unrecognised date '{value}'");

    public static DateTime ParseTimestamp(string value) =>
        DateTime.TryParseExact(value, TimestampFormats, CultureInfo.InvariantCulture, Lenient, out var parsed)
            ? parsed
            : throw new FeedParseException($"unrecognised timestamp '{value}'");

    static DateTime? ParseOptionalTimestamp(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : ParseTimestamp(value);

    // The export can render a user id as "Last, First (userid)"; keep only the id.
    [GeneratedRegex(@"\(([^()]+)\)\s*$")]
    private static partial Regex UserId();

    public static string ParseUserId(string value)
    {
        var match = UserId().Match(value);
        return match.Success ? match.Groups[1].Value.Trim() : value.Trim();
    }

    static bool TryParseNumber(string value, out double number) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);

    // The scheduling system stores a missing GPS position as 0.
    static double? ParseCoordinate(string value, string name, long lineNumber)
    {
        double number = 0;
        if (!string.IsNullOrWhiteSpace(value) && !TryParseNumber(value, out number))
            throw new FeedParseException($"line {lineNumber}: bad {name} '{value}'");
        return number == 0 ? null : number;
    }

    // Postgres TO_CHAR ... OF gives a bare hour offset like "-05"; append the minutes.
    static DateTimeOffset ParseGeneratedAt(string value)
    {
        var text = value.Trim();
        if (text.Length >= 3 && text[^3] is '+' or '-' && char.IsDigit(text[^2]) && char.IsDigit(text[^1]))
            text += ":00";
        // Without an offset, the time is UTC (as Postgres stores it).
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : throw new FeedParseException($"unrecognised generated_at '{value}'");
    }

    static Slot ParseSlot(Dictionary<string, string> values, long lineNumber)
    {
        // A fractional minute count is truncated toward zero.
        if (!TryParseNumber(values["open_minutes"], out var minutes) || Math.Abs(minutes) >= int.MaxValue)
            throw new FeedParseException($"line {lineNumber}: bad open_minutes '{values["open_minutes"]}'");
        return new Slot(
            ParseDate(values["work_date"]),
            values["tech_id"].Trim(),
            values["tech_name"].Trim(),
            ParseTimestamp(values["open_from"]),
            ParseTimestamp(values["open_until"]),
            (int)Math.Truncate(minutes),
            values["region"].Trim(),
            values["skills"].Trim()
        );
    }

    static Block ParseBlock(Dictionary<string, string> values, long lineNumber)
    {
        string Get(string name) => values.GetValueOrDefault(name, "");
        var kind = values["kind"].Trim().ToLowerInvariant();
        if (!BlockKinds.Contains(kind))
            throw new FeedParseException($"line {lineNumber}: unknown kind '{values["kind"]}'");
        return new Block
        {
            Kind = kind,
            WorkDate = ParseDate(values["work_date"]),
            TechId = values["tech_id"].Trim(),
            TechName = values["tech_name"].Trim(),
            StartsAt = ParseTimestamp(values["starts_at"]),
            EndsAt = ParseTimestamp(values["ends_at"]),
            RefId = values["ref_id"].Trim(),
            Status = values["status"].Trim(),
            Department = Get("department").Trim(),
            Region = values["region"].Trim(),
            Skills = values["skills"].Trim(),
            TaskType = Get("task_type").Trim(),
            ModifiedAt = ParseOptionalTimestamp(Get("modified_at")),
            ModifiedBy = ParseUserId(Get("modified_by")),
            EnrouteAt = ParseOptionalTimestamp(Get("enroute_at")),
            InprogressAt = ParseOptionalTimestamp(Get("inprogress_at")),
            PrereqsStatus = Get("pre-reqs status").Trim(),
            AddressIssue = values.TryGetValue("address_issue", out var issue) ? issue.Trim() : null,
            Latitude = ParseCoordinate(Get("latitude"), "latitude", lineNumber),
            Longitude = ParseCoordinate(Get("longitude"), "longitude", lineNumber),
            GpsPrecision = Get("gps_precision").Trim().ToUpperInvariant(),
        };
    }
}
