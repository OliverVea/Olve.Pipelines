namespace Olve.Pipelines.Pipelines;

/// <summary>
/// Derives a single pipeline-level health string from a pipeline's per-step <see cref="StepHealth"/>
/// strip, for the list view. A step is "running" while its latest job is <c>scheduled</c> or
/// <c>in-progress</c>, "failed" when it is <c>failed</c>, and "green" when it is <c>done</c>.
/// Precedence:
/// <list type="bullet">
/// <item><see cref="RunningUnhealthy"/> — something is running and at least one step has failed.</item>
/// <item><see cref="RunningHealthy"/> — something is running and nothing has failed.</item>
/// <item><see cref="Unhealthy"/> — nothing running but at least one step has failed.</item>
/// <item><see cref="Healthy"/> — nothing running, nothing failed, and every step is green.</item>
/// <item><see cref="Idle"/> — no steps, or a not-yet-fully-run pipeline (idle/cancelled/obsolete steps,
///   none running, none failed).</item>
/// </list>
/// </summary>
public static class PipelineStatus
{
    public const string Healthy = "Healthy";
    public const string RunningHealthy = "Running (Healthy)";
    public const string RunningUnhealthy = "Running (Unhealthy)";
    public const string Unhealthy = "Unhealthy";
    public const string Idle = "Idle";

    public static string Compute(IReadOnlyCollection<StepHealth> steps)
    {
        if (steps.Count == 0)
            return Idle;

        var anyRunning = steps.Any(s => s.Status is "scheduled" or "in-progress");
        var anyFailed = steps.Any(s => s.Status is "failed");

        if (anyRunning)
            return anyFailed ? RunningUnhealthy : RunningHealthy;

        if (anyFailed)
            return Unhealthy;

        return steps.All(s => s.Status is "done") ? Healthy : Idle;
    }
}
