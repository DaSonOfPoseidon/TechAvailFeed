namespace TechAvail.Core.Parsing;

public sealed class FeedParseException(string message) : Exception(message);

// Legacy gap format: one open slot per row.
public sealed record Slot(
    DateOnly WorkDate,
    string TechId,
    string TechName,
    DateTime OpenFrom,
    DateTime OpenUntil,
    int OpenMinutes,
    string Region,
    string Skills
);

// One raw calendar block. Timestamps are the scheduling system's local time, as in the feed.
public sealed record Block
{
    public required string Kind { get; init; }
    public required DateOnly WorkDate { get; init; }
    public required string TechId { get; init; }
    public required string TechName { get; init; }
    public required DateTime StartsAt { get; init; }
    public required DateTime EndsAt { get; init; }
    public required string RefId { get; init; }
    public required string Status { get; init; }
    public string Department { get; init; } = "";
    public required string Region { get; init; }
    public required string Skills { get; init; }
    public string TaskType { get; init; } = "";
    public DateTime? ModifiedAt { get; init; }
    public string ModifiedBy { get; init; } = "";
    public DateTime? EnrouteAt { get; init; }
    public DateTime? InprogressAt { get; init; }
    public string PrereqsStatus { get; init; } = "";

    // null when the export has no ADDRESS_ISSUE column (unknown), "" when the address is fine.
    public string? AddressIssue { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string GpsPrecision { get; init; } = "";

    // The region set on the job or ticket itself in MBS, which can differ from Region (its
    // address's). null when the export has no SET_REGION column.
    public string? SetRegion { get; init; }
}

public sealed class ParsedFeed
{
    public required string Sha256 { get; init; }
    public string Format { get; set; } = "slots";
    public DateTimeOffset? GeneratedAt { get; set; }
    public List<Slot> Slots { get; } = [];
    public List<Block> Blocks { get; } = [];
    public int RowCount => Format == "blocks" ? Blocks.Count : Slots.Count;
}
