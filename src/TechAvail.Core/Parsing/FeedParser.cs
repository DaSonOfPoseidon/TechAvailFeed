using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TechAvail.Core.Parsing;

// Port of feed/parse.py. Accepts, rejects and reports (error messages included) exactly what the
// Python parser does; tools/TechAvail.Parity checks that against the real feed.
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

    static readonly string[] DateFormats = ["%Y-%m-%d", "%m-%d-%Y", "%m/%d/%Y"];

    // The scheduling system rewrites a seconds-precision value as "09-29-2026 12:03:34".
    static readonly string[] TimestampFormats =
    [
        "%Y-%m-%d %H:%M",
        "%Y-%m-%d %H:%M:%S",
        "%m-%d-%Y %H:%M:%S",
        "%m-%d-%Y %I:%M %p",
    ];

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

        using var rows = PyCsv.Read(text).GetEnumerator();
        List<string> header;
        try
        {
            if (!rows.MoveNext())
                throw new FeedParseException("file is empty, no header row");
            header = rows.Current;
        }
        catch (CsvException exc)
        {
            throw new FeedParseException($"bad header row: {exc.Message}");
        }

        var columns = header.Select(name => name.Trim().ToLowerInvariant()).ToList();
        if (columns.Contains("kind"))
            parsed.Format = "blocks";
        var required = parsed.Format == "blocks" ? BlockColumns : RequiredColumns;
        var missing = required.Where(name => !columns.Contains(name)).ToList();
        if (missing.Count > 0)
            throw new FeedParseException($"missing columns: {string.Join(", ", missing)}");
        var index = required.ToDictionary(name => name, columns.IndexOf);
        if (parsed.Format == "blocks")
            foreach (var name in OptionalBlockColumns.Where(columns.Contains))
                index[name] = columns.IndexOf(name);

        for (int lineNumber = 2; ; lineNumber++)
        {
            try
            {
                if (!rows.MoveNext())
                    break;
            }
            catch (CsvException exc)
            {
                throw new FeedParseException($"malformed CSV: {exc.Message}");
            }
            var row = rows.Current;
            if (row.All(string.IsNullOrWhiteSpace))
                continue;
            if (row.Count != columns.Count)
                throw new FeedParseException(
                    $"line {lineNumber}: expected {columns.Count} fields, got {row.Count}"
                );
            var values = index.ToDictionary(pair => pair.Key, pair => row[pair.Value]);
            parsed.GeneratedAt ??= ParseGeneratedAt(values["generated_at"]);
            if (parsed.Format == "blocks")
                parsed.Blocks.Add(ParseBlock(values, lineNumber));
            else
                parsed.Slots.Add(ParseSlot(values, lineNumber));
        }
        return parsed;
    }

    public static DateOnly ParseDate(string value)
    {
        var text = value.Trim();
        foreach (var format in DateFormats)
            if (PyTime.TryStrptime(text, format, out var parsed))
                return DateOnly.FromDateTime(parsed);
        throw new FeedParseException($"unrecognised date '{value}'");
    }

    public static DateTime ParseTimestamp(string value)
    {
        var text = value.Trim();
        foreach (var format in TimestampFormats)
            if (PyTime.TryStrptime(text, format, out var parsed))
                return parsed;
        throw new FeedParseException($"unrecognised timestamp '{value}'");
    }

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

    // Python's float(): optional sign, digits with single underscores between them, an optional
    // fraction and exponent, or inf/infinity/nan, with surrounding whitespace.
    [GeneratedRegex(
        @"^[+-]?(?:(?:[0-9](?:_?[0-9])*(?:\.(?:[0-9](?:_?[0-9])*)?)?|\.[0-9](?:_?[0-9])*)(?:[eE][+-]?[0-9](?:_?[0-9])*)?|inf|infinity|nan)$",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex PyFloat();

    static bool TryParseFloat(string value, out double number)
    {
        number = 0;
        var text = value.Trim();
        if (!PyFloat().IsMatch(text))
            return false;
        text = text.Replace("_", "").ToLowerInvariant();
        var negative = text.StartsWith('-');
        var unsigned = text.TrimStart('+', '-');
        if (unsigned is "inf" or "infinity")
            number = negative ? double.NegativeInfinity : double.PositiveInfinity;
        else if (unsigned == "nan")
            number = double.NaN;
        else
            number = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        return true;
    }

    // The scheduling system stores a missing GPS position as 0.
    static double? ParseCoordinate(string value, string name, int lineNumber)
    {
        double number = 0;
        if (!string.IsNullOrWhiteSpace(value) && !TryParseFloat(value, out number))
            throw new FeedParseException($"line {lineNumber}: bad {name} '{value}'");
        return number == 0 ? null : number;
    }

    // Postgres TO_CHAR ... OF gives a bare hour offset like "-05"; append the minutes.
    static DateTimeOffset ParseGeneratedAt(string value)
    {
        var text = value.Trim();
        if (text.Length >= 3 && text[^3] is '+' or '-' && char.IsDigit(text[^2]) && char.IsDigit(text[^1]))
            text += ":00";
        if (!PyTime.TryFromIsoFormat(text, out var parsed))
            throw new FeedParseException($"unrecognised generated_at '{value}'");
        return parsed;
    }

    static Slot ParseSlot(Dictionary<string, string> values, int lineNumber)
    {
        // int(float(x)): the fraction is truncated toward zero.
        if (
            !TryParseFloat(values["open_minutes"], out var minutes)
            || !double.IsFinite(minutes)
            || Math.Abs(minutes) >= int.MaxValue
        )
            throw new FeedParseException(
                $"line {lineNumber}: bad open_minutes '{values["open_minutes"]}'"
            );
        var workDate = ParseDate(values["work_date"]);
        var techId = values["tech_id"].Trim();
        var techName = values["tech_name"].Trim();
        var openFrom = ParseTimestamp(values["open_from"]);
        var openUntil = ParseTimestamp(values["open_until"]);
        return new Slot(
            workDate,
            techId,
            techName,
            openFrom,
            openUntil,
            (int)Math.Truncate(minutes),
            values["region"].Trim(),
            values["skills"].Trim()
        );
    }

    // Fields are read in the Python constructor's order, so the first bad value reported matches.
    static Block ParseBlock(Dictionary<string, string> values, int lineNumber)
    {
        string Get(string name) => values.GetValueOrDefault(name, "");
        var kind = values["kind"].Trim().ToLowerInvariant();
        if (!BlockKinds.Contains(kind))
            throw new FeedParseException($"line {lineNumber}: unknown kind '{values["kind"]}'");
        var workDate = ParseDate(values["work_date"]);
        var startsAt = ParseTimestamp(values["starts_at"]);
        var endsAt = ParseTimestamp(values["ends_at"]);
        var modifiedAt = ParseOptionalTimestamp(Get("modified_at"));
        var modifiedBy = ParseUserId(Get("modified_by"));
        var enrouteAt = ParseOptionalTimestamp(Get("enroute_at"));
        var inprogressAt = ParseOptionalTimestamp(Get("inprogress_at"));
        var latitude = ParseCoordinate(Get("latitude"), "latitude", lineNumber);
        var longitude = ParseCoordinate(Get("longitude"), "longitude", lineNumber);
        return new Block
        {
            Kind = kind,
            WorkDate = workDate,
            TechId = values["tech_id"].Trim(),
            TechName = values["tech_name"].Trim(),
            StartsAt = startsAt,
            EndsAt = endsAt,
            RefId = values["ref_id"].Trim(),
            Status = values["status"].Trim(),
            Department = Get("department").Trim(),
            Region = values["region"].Trim(),
            Skills = values["skills"].Trim(),
            TaskType = Get("task_type").Trim(),
            ModifiedAt = modifiedAt,
            ModifiedBy = modifiedBy,
            EnrouteAt = enrouteAt,
            InprogressAt = inprogressAt,
            PrereqsStatus = Get("pre-reqs status").Trim(),
            AddressIssue = values.TryGetValue("address_issue", out var issue) ? issue.Trim() : null,
            Latitude = latitude,
            Longitude = longitude,
            GpsPrecision = Get("gps_precision").Trim().ToUpperInvariant(),
        };
    }
}
