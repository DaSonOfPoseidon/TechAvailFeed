using Microsoft.AspNetCore.Mvc;
using TechAvail.Data;

namespace TechAvail.Api.Controllers;

[ApiController]
public sealed class HealthController(FeedStore store, TimeProvider clock) : ControllerBase
{
    // The monitoring probe: no auth, and 200 even before the first snapshot.
    [HttpGet("/health")]
    public object Get() => new { ok = true, snapshot = Snapshots.Info(store.SnapshotMeta(), clock.GetUtcNow()) };
}
