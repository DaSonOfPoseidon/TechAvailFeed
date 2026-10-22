using Microsoft.AspNetCore.Mvc;
using TechAvail.Core;
using TechAvail.Data;

namespace TechAvail.Api.Controllers;

public sealed class ArrivalsController(IFeedReads store, ApiSettings settings, TimeProvider clock) : V1Controller(store, settings, clock)
{
    static readonly TimeOnly Nine = new(9, 0);

    public sealed record RegionsResponse(DateOnly Date, List<string> Regions);

    // Today: the 8:00 jobs as of the 8:15 run, then the day so far from the latest snapshot. A past
    // day: each tech's completed jobs as of the day's last snapshot, then its 8:15 view.
    [HttpGet("arrivals.xlsx")]
    [Produces(Xlsx.ContentType)]
    public IActionResult Get(DateOnly? date, string? region)
    {
        var (day, sheets) = Sheets(date);
        if (region is not null)
            sheets = [.. sheets.Select(s => s with { Rows = [.. s.Rows.Where(r => r.Region == region)] })];
        var data = ArrivalExport.Workbook(Settings.Tz, Clock.GetUtcNow(), day, region, sheets);
        return Workbook(data, $"arrivals_{day:yyyy-MM-dd}.xlsx");
    }

    // From that day's snapshots, so a past day can list a region the latest snapshot no longer has.
    [HttpGet("arrivals/regions")]
    public RegionsResponse Regions(DateOnly? date)
    {
        var (day, sheets) = Sheets(date);
        return new RegionsResponse(
            day,
            [.. sheets.SelectMany(s => s.Rows).Select(r => r.Region).Where(r => r.Length > 0).Distinct().Order(StringComparer.Ordinal)]
        );
    }

    (DateOnly Day, ArrivalSheet[] Sheets) Sheets(DateOnly? date)
    {
        var today = Today();
        var day = date ?? today;
        Check(day <= today, "date can't be in the future");
        var snapshots = new OutcomeHistory(Store, Settings.Tz, Clock).Snapshots();
        var eight = Arrivals.FindEightFifteen(snapshots, day);
        if (day == today && eight is null && TimeOnly.FromDateTime(LocalNow()) < Nine)
            throw new ApiException(404, $"the 8:15 run for {day:yyyy-MM-dd} hasn't arrived yet");
        var last = Arrivals.FindLastOn(snapshots, day) ?? throw new ApiException(404, $"no snapshot on {day:yyyy-MM-dd}");

        var eightSheet = eight is null
            ? new ArrivalSheet("Arrivals 8 AM", null, [], Highlight: true, Missing: $"no 8:15 run on {day:yyyy-MM-dd}")
            : new ArrivalSheet("Arrivals 8 AM", eight.At, Arrivals.EightAm(Store.WorkBlocks(eight.Id), day, eight.At), Highlight: true);
        var lastBlocks = Store.WorkBlocks(last.Id);
        return (
            day,
            day == today
                ? [eightSheet, new("Today so far", last.At, Arrivals.SoFar(lastBlocks, day, last.At), Highlight: true)]
                : [new("Completed", last.At, Arrivals.Completed(lastBlocks, day, last.At), Highlight: false), eightSheet]
        );
    }
}
