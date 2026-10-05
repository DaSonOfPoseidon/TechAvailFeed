using Microsoft.AspNetCore.Mvc;
using TechAvail.Core;
using TechAvail.Core.Parsing;
using TechAvail.Data;

namespace TechAvail.Api.Controllers;

public sealed class MapController(FeedStore store, ApiSettings settings, TimeProvider clock) : V1Controller(store, settings, clock)
{
    static readonly string[] MapKinds = ["job", "ticket", "job_unassigned", "ticket_unassigned"];

    public sealed record Tech(string TechId, string TechName);

    // Live jobs and tickets in [start, end], one point per job (a two-tech job lists both techs).
    // Work takes its address's region; without one, the tech's shift region that day. Points
    // without coordinates are listed as unmapped, without the lat/lon keys.
    internal static (List<OrderedDictionary<string, object?>> Points, List<OrderedDictionary<string, object?>> Unmapped) Points(
        List<Block> blocks,
        DateOnly start,
        DateOnly end,
        string? region,
        string? kind,
        bool exact
    )
    {
        var regions = new Dictionary<(string, DateOnly), string>();
        foreach (var b in blocks.Where(b => b.Kind == "shift_tc"))
            regions[(b.TechId, b.WorkDate)] = b.Region;
        foreach (var b in blocks.Where(b => b.Kind == "shift"))
            regions[(b.TechId, b.WorkDate)] = b.Region;
        var found = new OrderedDictionary<(string, string), OrderedDictionary<string, object?>>();
        foreach (var b in blocks.OrderBy(b => b.StartsAt).ThenBy(b => b.TechName, StringComparer.Ordinal))
        {
            var baseKind = b.Kind.EndsWith("_unassigned", StringComparison.Ordinal) ? b.Kind[..^"_unassigned".Length] : b.Kind;
            if (!MapKinds.Contains(b.Kind) || b.WorkDate < start || b.WorkDate > end)
                continue;
            if (
                (Availability.NotBusy.TryGetValue(baseKind, out var statuses) && statuses.Contains(b.Status))
                || (kind is not null && baseKind != kind)
            )
                continue;
            var place = b.Region.Length > 0 ? b.Region : regions.GetValueOrDefault((b.TechId, b.WorkDate), "");
            if (region is not null && place != region)
                continue;
            if (found.TryGetValue((baseKind, b.RefId), out var existing))
            {
                if (b.TechId.Length > 0)
                    ((List<Tech>)existing["techs"]!).Add(new Tech(b.TechId, b.TechName));
                continue;
            }
            var (lat, lon) = Coords.Public(b.Latitude, b.Longitude, exact);
            found[(baseKind, b.RefId)] = new OrderedDictionary<string, object?>
            {
                ["ref_id"] = b.RefId,
                ["kind"] = baseKind,
                ["assigned"] = b.Kind is "job" or "ticket",
                ["status"] = b.Status,
                ["work_date"] = b.WorkDate,
                ["starts_at"] = b.StartsAt,
                ["ends_at"] = b.EndsAt,
                ["region"] = place,
                ["techs"] = b.TechId.Length > 0 ? new List<Tech> { new(b.TechId, b.TechName) } : new List<Tech>(),
                ["lat"] = lat,
                ["lon"] = lon,
                ["gps_precision"] = b.GpsPrecision,
                ["address_issue"] = b.AddressIssue,
            };
        }
        var points = found.Values.Where(p => p["lat"] is not null).ToList();
        var unmapped = found.Values.Where(p => p["lat"] is null).ToList();
        foreach (var p in unmapped)
        {
            p.Remove("lat");
            p.Remove("lon");
        }
        return (points, unmapped);
    }

    // Jobs as map points with rounded coordinates.
    [HttpGet("map")]
    public OrderedDictionary<string, object?> Get(DateOnly? start, string? region, string? kind, int days = 1)
    {
        CheckDays(days);
        Check(kind is null or "job" or "ticket", "kind must be job or ticket");
        var (blocks, snapshot) = Latest();
        var (from, to) = Window(start, days);
        var (points, unmapped) = Points(blocks, from, to, region, kind, Settings.ExactCoords);
        return new OrderedDictionary<string, object?>
        {
            ["snapshot"] = Info(snapshot),
            ["filters"] = new OrderedDictionary<string, object?> { ["region"] = region, ["kind"] = kind },
            ["start"] = from,
            ["end"] = to,
            ["exact"] = Settings.ExactCoords,
            ["points"] = points,
            ["unmapped"] = unmapped,
        };
    }
}
