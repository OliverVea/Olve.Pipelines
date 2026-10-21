using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Versions;

namespace Olve.Pipelines.Pipelines;

/// <summary>
/// Health of a pipeline over its runs (spec §7). <see cref="Status"/> is a <see cref="PipelineStatus"/>
/// value: running when any run is Running, else from the latest settled run (Succeeded/Failed —
/// cancelled and superseded runs say nothing about health, and a run that skipped steps still succeeded).
/// </summary>
/// <param name="LastRun">The newest run, whatever its status.</param>
/// <param name="LastSettledRun">The newest Succeeded or Failed run.</param>
/// <param name="Activation">Active version or the switch in progress (null = no version yet).</param>
/// <param name="HeldRuns">Running runs held at a promotion gate.</param>
public record PipelineHealth(
    string Status,
    RunSummary? LastRun,
    RunSummary? LastSettledRun,
    int RunningRuns,
    PipelineActivation? Activation,
    bool Draining,
    string[] BlockedSteps,
    RunSummary[] HeldRuns);

/// <summary>Computes <see cref="PipelineHealth"/>. Stateless query; transient.</summary>
public class PipelineHealthService(
    RunService runs,
    RunViewService views,
    PipelineActivationService activations,
    StepGateService gates)
{
    public PipelineHealth Get(Id<Pipeline> pipelineId)
    {
        var all = runs.ListByPipeline(pipelineId); // newest first
        var running = all.Where(r => r.Status is RunStatus.Running).ToList();
        var lastSettled = all.FirstOrDefault(r => r.Status is RunStatus.Succeeded or RunStatus.Failed);
        var activation = activations.TryGet(pipelineId, out var a) ? a : null;

        return new PipelineHealth(
            Compute(running.Count > 0, lastSettled?.Status),
            all.Count > 0 ? views.Summarize(all[0]) : null,
            lastSettled is null ? null : views.Summarize(lastSettled),
            running.Count,
            activation,
            activation is PipelineActivation.Draining,
            gates.ListBlocked(pipelineId).ToArray(),
            running.Where(r => r.HeldAt is not null).Select(views.Summarize).ToArray());
    }

    public static string Compute(bool anyRunning, RunStatus? lastSettled) => (anyRunning, lastSettled) switch
    {
        (true, RunStatus.Failed) => PipelineStatus.RunningUnhealthy,
        (true, _) => PipelineStatus.RunningHealthy,
        (false, RunStatus.Failed) => PipelineStatus.Unhealthy,
        (false, RunStatus.Succeeded) => PipelineStatus.Healthy,
        _ => PipelineStatus.Idle,
    };
}
