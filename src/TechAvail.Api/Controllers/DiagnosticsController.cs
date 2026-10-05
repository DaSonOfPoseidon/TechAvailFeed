using Microsoft.AspNetCore.Mvc;
using TechAvail.Core;
using TechAvail.Core.Parsing;
using TechAvail.Data;

namespace TechAvail.Api.Controllers;

public sealed class DiagnosticsController(FeedStore store, ApiSettings settings, TimeProvider clock) : V1Controller(store, settings, clock)
{
    public record Summary(string Id, string Group, string Title, string Severity, string Description, bool Available, int Count);

    public sealed record Detail(
        string Id,
        string Group,
        string Title,
        string Severity,
        string Description,
        bool Available,
        int Count,
        List<OrderedDictionary<string, object?>> Rows
    );

    public sealed record DiagnosticsResponse(SnapshotInfo? Snapshot, List<Summary> Summary, List<Detail> Checks);

    // Data-quality checks: double bookings, work outside shifts, stale open work, setup gaps.
    [HttpGet("diagnostics")]
    public DiagnosticsResponse Get(string? group, string? check)
    {
        var (blocks, snapshot) = Latest();
        var found = Diagnostics.Diagnose(blocks, Today());
        Check(
            group is null || Diagnostics.Groups.Contains(group),
            $"unknown group {PyText.Repr(group ?? "")}; use one of {string.Join(", ", Diagnostics.Groups)}"
        );
        Check(check is null || found.Any(c => c.Id == check), $"unknown check {PyText.Repr(check ?? "")}");
        var selected = found.Where(c => (group is null || c.Group == group) && (check is null || c.Id == check)).ToList();
        return new DiagnosticsResponse(
            Info(snapshot),
            [.. selected.Select(c => new Summary(c.Id, c.Group, c.Title, c.Severity, c.Description, c.Available, c.Rows.Count))],
            [.. selected.Select(c => new Detail(c.Id, c.Group, c.Title, c.Severity, c.Description, c.Available, c.Rows.Count, c.Rows))]
        );
    }
}
