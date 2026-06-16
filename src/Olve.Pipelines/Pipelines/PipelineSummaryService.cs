using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Pipelines.Sync;

namespace Olve.Pipelines.Pipelines;

/// <summary>
/// Assembles the list-page summaries (<see cref="PipelineSummary"/>) — one card per pipeline with
/// its bound repo and per-step health strip — so the client makes a single call instead of an N+1
/// fan-out of steps+jobs per pipeline. Stateless query service over the existing stores.
/// </summary>
public class PipelineSummaryService(
    PipelineService pipelines,
    ProductionStepService productionSteps,
    ProcessingStepService processingSteps,
    PipelineConfigBindingService bindings,
    JobService jobs)
{
    public IReadOnlyList<PipelineSummary> List()
    {
        var result = new List<PipelineSummary>();

        foreach (var pipeline in pipelines.List())
        {
            var repo = bindings.GetByPipelineId(pipeline.Id).TryPickProblems(out _, out var binding)
                ? null
                : binding.Repo;

            var (latestProduction, latestProcessing) = jobs.GetLatestJobsByStep(pipeline.Id);

            var steps = new List<StepHealth>();

            if (!productionSteps.GetByPipelineId(pipeline.Id).TryPickProblems(out _, out var prod))
            {
                foreach (var step in prod)
                {
                    var status = latestProduction.TryGetValue(step.Id, out var job)
                        ? job.Status.Discriminator()
                        : "idle";
                    steps.Add(new StepHealth(step.Name, "production", status));
                }
            }

            if (!processingSteps.GetByPipelineId(pipeline.Id).TryPickProblems(out _, out var proc))
            {
                foreach (var step in proc)
                {
                    var status = latestProcessing.TryGetValue(step.Id, out var job)
                        ? job.Status.Discriminator()
                        : "idle";
                    steps.Add(new StepHealth(step.Name, "processing", status));
                }
            }

            result.Add(new PipelineSummary(pipeline.Id, pipeline.Name, repo, steps.ToArray()));
        }

        return result;
    }
}
