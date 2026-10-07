using ClosedXML.Excel;
using TechAvail.Core;
using Column = TechAvail.Api.Xlsx.Column;

namespace TechAvail.Api;

// The data-quality checks as a workbook: one row per finding, with the check-specific fields in Detail.
public static class DiagnosticsExport
{
    static readonly Column[] Columns =
    [
        new("Check"),
        new("Severity"),
        new("Ref"),
        new("Kind"),
        new("Status"),
        new("Date", Xlsx.Date, 12),
        new("Starts", Xlsx.Time, 17),
        new("Ends", Xlsx.Time, 17),
        new("Tech id"),
        new("Tech"),
        new("Region"),
        new("Detail", Width: 60),
    ];

    static readonly string[] Fields = ["ref_id", "kind", "status", "work_date", "starts_at", "ends_at", "tech_id", "tech_name", "region"];

    // Fixed columns for the job or tech, and whatever else a check reports as "name: value".
    static List<object?[]> Rows(IEnumerable<Check> checks) =>
    [
        .. checks.SelectMany(check =>
            check.Rows.Select(row =>
            {
                var detail = string.Join(
                    "; ",
                    row.Where(f => !Fields.Contains(f.Key)).Select(f => $"{f.Key.Replace('_', ' ')}: {Xlsx.Text(f.Value)}")
                );
                return (object?[])[check.Title, check.Severity, .. Fields.Select(f => row.GetValueOrDefault(f)), detail];
            })
        ),
    ];

    internal static void WriteSheet(XLWorkbook wb, List<Check> checks) => Xlsx.WriteSheet(wb, "Diagnostics", Columns, Rows(checks));

    // Rows per check, or "not in feed yet" for a check whose columns the feed doesn't send.
    internal static IEnumerable<(string, object?)> Counts(List<Check> checks) =>
        checks.Select(c => (c.Title, (object?)(c.Available ? c.Rows.Count : "not in feed yet")));

    public static byte[] Workbook(
        TimeZoneInfo tz,
        DateTimeOffset now,
        long snapshotId,
        DateTimeOffset? generatedAt,
        string? group,
        string? check,
        List<Check> checks
    )
    {
        using var wb = new XLWorkbook();
        WriteSheet(wb, checks);
        new AboutSheet(wb)
            .ExportedAt(tz, now)
            .Add("Snapshot id", snapshotId)
            .Add("Snapshot generated at", generatedAt is { } g ? TimeZoneInfo.ConvertTime(g, tz).DateTime : null)
            .Filters(new OrderedDictionary<string, object?> { ["group"] = group, ["check"] = check })
            .Section("Diagnostic", "Rows", Counts(checks));
        return Xlsx.Save(wb);
    }
}
