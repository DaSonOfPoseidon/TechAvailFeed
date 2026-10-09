using Microsoft.AspNetCore.Mvc;
using TechAvail.Core;
using TechAvail.Data;

namespace TechAvail.Api.Controllers;

public sealed class JeopardyController(FeedStore store, ApiSettings settings, TimeProvider clock) : V1Controller(store, settings, clock)
{
    // The VP's status update (10 AM, 1 PM, 3 PM, 5 PM). With at, the run for that time; else the day's latest.
    [HttpGet("jeopardy.xlsx")]
    [Produces(Xlsx.ContentType)]
    public IActionResult Get(DateOnly? date, TimeOnly? at, string? region)
    {
        var today = Today();
        var day = date ?? today;
        Check(day <= today, "date can't be in the future");
        Check(region is null || StatusUpdate.RegionNames.Contains(region), $"region must be one of {string.Join(", ", StatusUpdate.RegionNames)}");
        if (at is { } time && day == today && time > TimeOnly.FromDateTime(LocalNow()))
            throw new ApiException(404, $"{time:HH:mm} hasn't come yet");
        var snapshots = new OutcomeHistory(Store, Settings.Tz, Clock).Snapshots();
        var snapshot =
            (at is { } t ? StatusUpdate.FindAt(snapshots, day, t) : Arrivals.FindLastOn(snapshots, day))
            ?? throw new ApiException(404, $"no snapshot on {day:yyyy-MM-dd}" + (at is { } a ? $" by {a:HH:mm}" : ""));

        var report = StatusUpdate.Build(Store.WorkBlocks(snapshot.Id), day, snapshot.At);
        if (region is not null)
            report = new StatusReport(
                [.. report.Regions.Where(r => r.Region == region)],
                [.. report.Concerns.Where(c => c.Region == region)],
                [.. report.Jeopardy.Where(r => StatusUpdate.RegionFor(r.Job.Region) == region)]
            );
        var data = JeopardyExport.Workbook(Settings.Tz, Clock.GetUtcNow(), day, at, snapshot.At, region, report);
        var suffix = at is { } stamp ? $"_{stamp:HHmm}" : "";
        return Workbook(data, $"jeopardy_{day:yyyy-MM-dd}{suffix}.xlsx");
    }
}
