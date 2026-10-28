namespace Olve.Pipelines.Pipelines;

/// <summary>
/// Pipeline-level health strings for list views, computed over runs by <see cref="PipelineHealthService.Compute"/>:
/// <list type="bullet">
/// <item><see cref="RunningUnhealthy"/> — a run is running and the latest settled run failed.</item>
/// <item><see cref="RunningHealthy"/> — a run is running and the latest settled run did not fail.</item>
/// <item><see cref="Unhealthy"/> — nothing running; the latest settled run failed.</item>
/// <item><see cref="Healthy"/> — nothing running; the latest settled run succeeded (skipped steps are not faults).</item>
/// <item><see cref="Idle"/> — no settled run and nothing running.</item>
/// </list>
/// </summary>
public static class PipelineStatus
{
    public const string Healthy = "Healthy";
    public const string RunningHealthy = "Running (Healthy)";
    public const string RunningUnhealthy = "Running (Unhealthy)";
    public const string Unhealthy = "Unhealthy";
    public const string Idle = "Idle";
}
