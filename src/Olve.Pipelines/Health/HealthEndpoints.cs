using Olve.Pipelines.Shared.Persistence;
using IResults = Microsoft.AspNetCore.Http.Results;

namespace Olve.Pipelines.Health;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        // Liveness: the process is up. Independent of persistence state.
        app.MapGet("/api/health", () => IResults.Ok())
            .AllowAnonymous();

        // Readiness: every snapshot store has confirmed its load. Until then we return 503 so
        // Kubernetes keeps the pod out of the Service rather than serving empty state.
        app.MapGet("/api/ready", (IPersistenceReadiness readiness) =>
                readiness.IsReady
                    ? IResults.Ok()
                    : IResults.StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status503ServiceUnavailable))
            .AllowAnonymous();
    }
}
