using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using TechAvail.Core.Parsing;
using TechAvail.Data;

namespace TechAvail.Api.Controllers;

// Shared plumbing for /api/v1: the key check, the served snapshot and local "now".
[ApiController]
[ApiKey]
[Route("api/v1")]
public abstract class V1Controller(FeedStore store, ApiSettings settings, TimeProvider clock) : ControllerBase
{
    protected FeedStore Store { get; } = store;
    protected ApiSettings Settings { get; } = settings;
    protected TimeProvider Clock { get; } = clock;

    // Naive local time, like the block timestamps.
    protected DateTime LocalNow() => TimeZoneInfo.ConvertTime(Clock.GetUtcNow(), Settings.Tz).DateTime;

    protected DateOnly Today() => DateOnly.FromDateTime(LocalNow());

    protected SnapshotInfo? Info(SnapshotRow snapshot) => Snapshots.Info(snapshot, Clock.GetUtcNow());

    protected SnapshotInfo? Info(SnapshotMeta? meta) => Snapshots.Info(meta, Clock.GetUtcNow());

    // The served blocks snapshot, or a 404 before there is one.
    protected (List<Block> Blocks, SnapshotRow Snapshot) Latest()
    {
        var found = Store.Latest();
        if (found?.Blocks is null)
            throw new ApiException(404, "no blocks snapshot yet");
        return (found.Blocks, found.Snapshot);
    }
}

// Thrown from an endpoint to answer with FastAPI's {"detail": ...} body.
public sealed class ApiException(int status, string detail) : Exception(detail)
{
    public int Status { get; } = status;
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
