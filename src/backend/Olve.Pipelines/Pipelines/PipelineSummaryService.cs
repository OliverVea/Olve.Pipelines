using System.Diagnostics.CodeAnalysis;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Versions;

namespace Olve.Pipelines.Pipelines;

/// <summary>Assembles a pipeline with its repo, health and stages (the API's whole pipeline). Stateless query; transient.</summary>
public class PipelineSummaryService(
    PipelineService pipelines,
    PipelineConfigBindingService bindings,
    PipelineHealthService health,
    PipelineActivationService activations,
    RunPlanCache plans,
    RunViewService views,
    Configs.ConfigSnapshotService snapshots)
{
    public IReadOnlyList<PipelineSummary> List() => [.. pipelines.List().Select(Summarize)];

    public bool TryGet(Id<Pipeline> id, [NotNullWhen(true)] out PipelineSummary? summary)
    {
        summary = pipelines.TryGet(id, out var pipeline) ? Summarize(pipeline) : null;
        return summary is not null;
    }

    private PipelineSummary Summarize(Pipeline p) => new(
        p.Id, p.Name, p.Description,
        bindings.GetByPipelineId(p.Id).TryPickProblems(out _, out var binding) ? null : binding.Repo,
        health.Get(p.Id),
        Stages(p.Id));

    private PipelineStageSummary[] Stages(Id<Pipeline> pipelineId)
    {
        if (!activations.TryGet(pipelineId, out var activation))
            return [];
        var versionId = activation switch
        {
            PipelineActivation.Draining d => d.ToVersionId,
            PipelineActivation.Active a => a.VersionId,
            _ => throw new InvalidOperationException($"Unknown activation {activation.GetType().Name}."),
        };
        if (plans.ForCurrentConfig(versionId, snapshots).TryPickProblems(out _, out var plan))
            return [];

        return plan.Stages.Select(stage => new PipelineStageSummary(
            stage.Conditions
                .SelectMany(c => c.Fields.OrderBy(f => f.Key, StringComparer.Ordinal))
                .Select(f => new RunStageCondition(f.Key, f.Value.ToArray()))
                .ToArray(),
            stage.Steps.Select(step =>
            {
                var latest = views.History(pipelineId, step.Name, 1).FirstOrDefault();
                return new PipelineStepSummary(step.Name, latest?.Status, latest?.RunNumber,
                    latest is null ? null : latest.FinishedAt ?? latest.StartedAt ?? latest.CreatedAt);
            }).ToArray())).ToArray();
    }
}
