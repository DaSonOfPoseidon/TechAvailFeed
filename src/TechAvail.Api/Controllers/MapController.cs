using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using TechAvail.Core;
using TechAvail.Core.Parsing;
using TechAvail.Data;

namespace TechAvail.Api.Controllers;

public sealed class MapController(IFeedReads store, ApiSettings settings, TimeProvider clock) : V1Controller(store, settings, clock)
{
    static readonly string[] MapKinds = ["job", "ticket", "job_unassigned", "ticket_unassigned"];

    public sealed record Tech(string TechId, string TechName);

    // Unmapped points (no coordinates) have no lat/lon keys.
    public sealed record Point(
        string RefId,
        string Kind,
        bool Assigned,
        string Status,
        DateOnly WorkDate,
        DateTime StartsAt,
        DateTime EndsAt,
        string Region,
        List<Tech> Techs,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Lat,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Lon,
        string GpsPrecision,
        string? AddressIssue
    );

    public sealed record MapFilters(string? Region, string? Kind, bool Completed);

    public sealed record MapResponse(
        SnapshotInfo? Snapshot,
        MapFilters Filters,
        DateOnly Start,
        DateOnly End,
        bool Exact,
        List<Point> Points,
        List<Point> Unmapped
    );

    // Jobs and tickets in [start, end], one point per job (a two-tech job lists both techs).
    // Canceled work is left out, and completed work too unless `completed`. Work takes its address's region; without one, the tech's shift region that day. Points
    // without coordinates are listed as unmapped, without the lat/lon keys.
    internal static (List<Point> Points, List<Point> Unmapped) Points(
        List<Block> blocks,
        DateOnly start,
        DateOnly end,
        string? region,
        string? kind,
        bool completed,
        bool exact
    )
    {
        var regions = new Dictionary<(string, DateOnly), string>();
        foreach (var b in blocks.Where(b => b.Kind == "shift_tc"))
            regions[(b.TechId, b.WorkDate)] = b.Region;
        foreach (var b in blocks.Where(b => b.Kind == "shift"))
            regions[(b.TechId, b.WorkDate)] = b.Region;
        var found = new OrderedDictionary<(string, string), Point>();
        foreach (var b in blocks.OrderBy(b => b.StartsAt).ThenBy(b => b.TechName, StringComparer.Ordinal))
        {
            var baseKind = b.Kind.EndsWith("_unassigned", StringComparison.Ordinal) ? b.Kind[..^"_unassigned".Length] : b.Kind;
            if (!MapKinds.Contains(b.Kind) || b.WorkDate < start || b.WorkDate > end)
                continue;
            if (
                Outcomes.CanceledStatuses[baseKind].Contains(b.Status)
                || (!completed && Outcomes.CompletedStatuses[baseKind].Contains(b.Status))
                || (kind is not null && baseKind != kind)
            )
                continue;
            var place = b.Region.Length > 0 ? b.Region : regions.GetValueOrDefault((b.TechId, b.WorkDate), "");
            if (region is not null && place != region)
                continue;
            if (found.TryGetValue((baseKind, b.RefId), out var existing))
            {
                if (b.TechId.Length > 0)
                    existing.Techs.Add(new Tech(b.TechId, b.TechName));
                continue;
            }
            var (lat, lon) = Coords.Public(b.Latitude, b.Longitude, exact);
            found[(baseKind, b.RefId)] = new Point(
                b.RefId,
                baseKind,
                b.Kind is "job" or "ticket",
                b.Status,
                b.WorkDate,
                b.StartsAt,
                b.EndsAt,
                place,
                b.TechId.Length > 0 ? [new(b.TechId, b.TechName)] : [],
                lat,
                lon,
                b.GpsPrecision,
                b.AddressIssue
            );
        }
        return ([.. found.Values.Where(p => p.Lat is not null)], [.. found.Values.Where(p => p.Lat is null)]);
    }

    // Jobs as map points with rounded coordinates.
    [HttpGet("map")]
    public MapResponse Get(DateOnly? start, string? region, string? kind, int days = 1, bool completed = true)
    {
        CheckDays(days);
        Check(kind is null or "job" or "ticket", "kind must be job or ticket");
        var (blocks, snapshot) = Latest();
        var (from, to) = Window(start, days);
        var (points, unmapped) = Points(blocks, from, to, region, kind, completed, Settings.ExactCoords);
        return new MapResponse(Info(snapshot), new MapFilters(region, kind, completed), from, to, Settings.ExactCoords, points, unmapped);
    }
}
