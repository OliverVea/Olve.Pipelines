using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Sync;

namespace Olve.Pipelines.Pipelines;

/// <summary>
/// Assembles the list-page summaries (<see cref="PipelineSummary"/>) — one card per pipeline with
/// its bound repo and per-step health strip. Stateless query service over the existing stores.
/// </summary>
public class PipelineSummaryService(
    PipelineService pipelines,
    PipelineConfigBindingService bindings,
    AttemptService attempts)
{
    public IReadOnlyList<PipelineSummary> List()
    {
        var result = new List<PipelineSummary>();

        foreach (var pipeline in pipelines.List())
        {
            var repo = bindings.GetByPipelineId(pipeline.Id).TryPickProblems(out _, out var binding)
                ? null
                : binding.Repo;

            var steps = attempts.ListByPipeline(pipeline.Id)
                .GroupBy(a => a.StepName)
                .Select(g => g.MaxBy(a => (a.RunNumber, a.Number))!)
                .OrderBy(a => a.StepName, StringComparer.Ordinal)
                .Select(a => new StepHealth(a.StepName, a.Status.Discriminator(), a.LastChangedAt()))
                .ToArray();

            var lastChangedAt = steps.Length > 0 ? steps.Max(s => s.LastChangedAt) : null;

            result.Add(new PipelineSummary(pipeline.Id, pipeline.Name, repo, PipelineStatus.Compute(steps), steps, lastChangedAt));
        }

        return result;
    }
}
