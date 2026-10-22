using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using TechAvail.Core.Parsing;
using TechAvail.Data;

namespace TechAvail.Api.Controllers;

[ApiController]
[ApiKey]
[Route("api/v1")]
public abstract class V1Controller(IFeedReads store, ApiSettings settings, TimeProvider clock) : ControllerBase
{
    protected IFeedReads Store { get; } = store;
    protected ApiSettings Settings { get; } = settings;
    protected TimeProvider Clock { get; } = clock;

    // Naive local time, like the block timestamps.
    protected DateTime LocalNow() => TimeZoneInfo.ConvertTime(Clock.GetUtcNow(), Settings.Tz).DateTime;

    protected DateOnly Today() => DateOnly.FromDateTime(LocalNow());

    protected SnapshotInfo? Info(SnapshotRow snapshot) => Snapshots.Info(snapshot, Clock.GetUtcNow());

    protected SnapshotInfo? Info(SnapshotMeta? meta) => Snapshots.Info(meta, Clock.GetUtcNow());

    public const int MaxDays = 62;
    public const int MaxHistoryDays = 366;

    protected static void Check(bool valid, string detail)
    {
        if (!valid)
            throw new ApiException(422, detail);
    }

    protected static void CheckDays(int days, int max = MaxDays) =>
        Check(days is >= 1 && days <= max, $"days must be between 1 and {max}");

    protected static void CheckCalendar(string calendar) =>
        Check(calendar is "install" or "tc", "calendar must be install or tc");

    protected (DateOnly Start, DateOnly End) Window(DateOnly? start, int days)
    {
        var from = start ?? Today();
        return (from, from.AddDays(days - 1));
    }

    // A workbook download. The header is set by hand: File(..., name) would add a filename* parameter.
    protected FileContentResult Workbook(byte[] data, string filename)
    {
        Response.Headers.ContentDisposition = $"attachment; filename=\"{filename}\"";
        return File(data, Xlsx.ContentType);
    }

    protected (DateOnly Start, DateOnly End) HistoryWindow(DateOnly? start, DateOnly? end)
    {
        var to = end ?? Today();
        var from = start ?? to.AddDays(-29);
        Check(from <= to && to.DayNumber - from.DayNumber < MaxHistoryDays, $"need start <= end, at most {MaxHistoryDays} days");
        return (from, to);
    }

    protected (List<Block> Blocks, SnapshotRow Snapshot) Latest()
    {
        var found = Store.Latest();
        if (found?.Blocks is null)
            throw new ApiException(404, "no blocks snapshot yet");
        return (found.Blocks, found.Snapshot);
    }
}

public sealed class ApiException(int status, string detail) : Exception(detail)
{
    public int Status { get; } = status;
}

// `region=(none)` selects the work and techs with no region: an empty query value binds as null
// (no filter), so the blank region needs a name of its own on the wire.
public sealed class BlankRegionFilter : IActionFilter
{
    public const string NoRegion = "(none)";

    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.ActionArguments.TryGetValue("region", out var region) && region is NoRegion)
            context.ActionArguments["region"] = "";
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}

public sealed class ApiExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is ApiException error)
        {
            context.Result = Errors.Detail(error.Status, error.Message);
            context.ExceptionHandled = true;
        }
    }
}
