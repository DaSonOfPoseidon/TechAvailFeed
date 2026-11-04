namespace TechAvail.Core;

public sealed record IncompleteReason(string Code, string Bucket, string Owner);

// Rules: docs/business-rules.md#outcomes
public static class IncompleteReasons
{
    public const string Customer = "customer";
    public const string Socket = "socket";

    static readonly Dictionary<string, string> Buckets = new()
    {
        ["CX"] = "canceled",
        ["SX"] = "canceled",
        ["CR"] = "rescheduled",
        ["SR"] = "rescheduled",
        ["CU"] = "unscheduled",
        ["SU"] = "unscheduled",
    };

    // null for a blank code, a free-text one ("AdditionalW") or a deactivated one.
    public static IncompleteReason? Parse(string? code)
    {
        var parts = code?.Split('_', 2);
        if (parts is not [var prefix, _] || !Buckets.TryGetValue(prefix, out var bucket))
            return null;
        return new IncompleteReason(code!, bucket, prefix[0] == 'C' ? Customer : Socket);
    }
}
