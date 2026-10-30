using Olve.Pipelines.Api;
using Olve.Pipelines.Shared.Persistence;
using Olve.TypeSpec;

namespace Olve.Pipelines.Health;

/// <summary>Liveness: the process is up. Independent of persistence state.</summary>
[OperationHandler]
public sealed class HealthHandler(IConfiguration config) : IInstanceHealthHandler
{
    private static readonly string? Version = typeof(HealthHandler).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;

    public Task<InstanceHealthResponse> HandleAsync(InstanceHealthRequest request, CancellationToken cancellationToken)
    {
        var image = config["Deployment:Image"];
        return Task.FromResult<InstanceHealthResponse>(new HealthResponse
        {
            Status = "ok",
            Image = string.IsNullOrWhiteSpace(image) ? null : image,
            Version = Version,
        });
    }
}

/// <summary>
/// Readiness: every snapshot store has confirmed its load. Until then 503, so Kubernetes keeps the
/// pod out of the Service rather than serving empty state.
/// </summary>
[OperationHandler]
public sealed class ReadinessHandler(IPersistenceReadiness readiness) : IInstanceReadyHandler
{
    public Task<InstanceReadyResponse> HandleAsync(InstanceReadyRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<InstanceReadyResponse>(readiness.IsReady
            ? new InstanceReadyResponse.Ok()
            : new InstanceReadyResponse.ServiceUnavailable());
}
