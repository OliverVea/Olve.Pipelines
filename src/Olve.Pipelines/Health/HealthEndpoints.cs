using Olve.Pipelines.Shared.Persistence;
using IResults = Microsoft.AspNetCore.Http.Results;

namespace Olve.Pipelines.Health;

/// <param name="Image">The image this instance runs, when its deployer says (<c>Deployment:Image</c>).</param>
/// <param name="Version">The server's informational version.</param>
public record HealthResponse(string Status, string? Image, string? Version);

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        var version = typeof(HealthEndpoints).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;

        // Liveness: the process is up. Independent of persistence state.
        app.MapGet("/api/health", (IConfiguration config) =>
                IResults.Ok(new HealthResponse("ok", string.IsNullOrWhiteSpace(config["Deployment:Image"]) ? null : config["Deployment:Image"], version)))
            .WithName("GetHealth")
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
