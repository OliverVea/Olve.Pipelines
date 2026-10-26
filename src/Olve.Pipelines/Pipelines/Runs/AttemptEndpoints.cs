using Olve.Pipelines.Configuration;

namespace Olve.Pipelines.Pipelines.Runs;

/// <summary>Cross-pipeline attempt queries. Attempts are otherwise addressed run-scoped
/// (<c>/api/pipelines/{p}/runs/{n}/steps/{step}/…</c>, see <see cref="RunEndpoints"/>).</summary>
public static class AttemptEndpoints
{
    public static void MapAttemptEndpoints(this WebApplication app)
    {
        // What's running (active) or waiting for input (waiting), on this instance, oldest first, by address.
        app.MapGet("/api/attempts", Result<ActiveAttempt[]> (ActiveAttemptQuery query, string? status)
                => status switch
                {
                    null or "active" => query.List().ToArray(),
                    "waiting" => query.List(waitingOnly: true).ToArray(),
                    _ => ValidationProblem.ForField("status", "status must be 'active' or 'waiting' (got '{0}').", status),
                })
            .WithApiResultMapping<ActiveAttempt[]>()
            .WithName("ListActiveAttempts")
            .WithTags("Attempts")
            .WithSummary("List running attempts")
            .WithDescription("`status=active` (default): every waiting, scheduled or in-progress attempt across pipelines, oldest first. `status=waiting`: only the ones waiting for external input.")
            .RequireRead();
    }
}
