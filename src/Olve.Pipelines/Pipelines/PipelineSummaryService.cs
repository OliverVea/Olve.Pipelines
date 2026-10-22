using Olve.Pipelines.Pipelines.Sync;

namespace Olve.Pipelines.Pipelines;

/// <summary>Assembles the list-page summaries. Stateless query; transient.</summary>
public class PipelineSummaryService(
    PipelineService pipelines,
    PipelineConfigBindingService bindings,
    PipelineHealthService health)
{
    public IReadOnlyList<PipelineSummary> List()
        => pipelines.List()
            .Select(p => new PipelineSummary(
                p.Id, p.Name, p.Description,
                bindings.GetByPipelineId(p.Id).TryPickProblems(out _, out var binding) ? null : binding.Repo,
                health.Get(p.Id)))
            .ToList();
}
