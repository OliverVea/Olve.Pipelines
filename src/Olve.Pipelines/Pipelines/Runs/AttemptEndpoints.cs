using Olve.MinimalApi;

namespace Olve.Pipelines.Pipelines.Runs;

/// <summary>Cross-pipeline attempt queries. Attempts are otherwise addressed run-scoped
/// (<c>/api/pipelines/{p}/runs/{n}/steps/{step}/…</c>, see <see cref="RunEndpoints"/>).</summary>
public static class AttemptEndpoints
{
    public static void MapAttemptEndpoints(this WebApplication app)
    {
        // What's running: every scheduled/in-progress attempt on this instance, oldest first, by address.
        app.MapGet("/api/attempts", Result<ActiveAttempt[]> (ActiveAttemptQuery query, string? status)
                => status is null or "active"
                    ? query.List().ToArray()
                    : new ResultProblem("status must be 'active' (got '{0}').", status))
            .WithResultMapping<ActiveAttempt[]>()
            .WithName("ListActiveAttempts")
            .AllowAnonymous();
    }
}
