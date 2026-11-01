using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Retention;

namespace Olve.Pipelines.Jobs;

/// <summary>
/// Deletes a deleted pipeline's K8s Jobs of every role, running ones included (best effort; failures are
/// only logged). Retention can't do this: it leaves a Job whose pipeline it doesn't know, because the
/// namespace may be shared with another instance.
/// </summary>
public class PipelineJobCleanupService(IServiceProvider services, ILogger<PipelineJobCleanupService> logger)
{
    public static IReadOnlyList<ClusterJob> JobsOf(Id<Pipeline> pipelineId, IEnumerable<ClusterJob> jobs)
    {
        var label = pipelineId.Value.Value.ToString("N");
        return jobs.Where(j => j.Labels.GetValueOrDefault("olve-pipeline") == label).ToList();
    }

    public async Task HandlePipelineDeletedAsync(Pipeline pipeline, CancellationToken ct = default)
    {
        try
        {
            if (services.GetService<IKubernetesInventory>() is not { } inventory)
                return;
            var kubernetes = services.GetRequiredService<IKubernetesClient>();
            var ns = services.GetRequiredService<KubernetesOptions>().Namespace;

            var jobs = JobsOf(pipeline.Id, await inventory.ListJobsAsync(ns, ct));
            foreach (var job in jobs)
                await kubernetes.DeleteJobAsync(ns, job.Name, ct);
            logger.LogInformation("Deleted {JobCount} K8s Jobs of deleted pipeline '{PipelineName}' ('{PipelineId}')", jobs.Count, pipeline.Name, pipeline.Id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete the K8s Jobs of deleted pipeline '{PipelineName}' ('{PipelineId}')", pipeline.Name, pipeline.Id);
        }
    }
}
