using Olve.MinimalApi;
using Olve.Pipelines.Pipelines;
using Olve.Results;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.Kubernetes.Api;

public static class JobEndpoints
{
    public static void MapJobEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pipelines/{pipelineId:guid}/jobs");

        group.MapGet("/", Result<JobRecord[]> (
            PipelineService pipelines,
            JobTracker jobTracker,
            Guid pipelineId) =>
        {
            var pipelineIdTyped = new Id<Pipeline>(new Id(pipelineId));

            if (!pipelines.TryGet(pipelineIdTyped, out _))
                return Result.Failure<JobRecord[]>(new ResultProblem($"Pipeline '{pipelineId}' not found."));

            var jobs = jobTracker.GetByPipelineId(pipelineId);
            return Result.Success(jobs.ToArray());
        })
        .WithResultMapping<JobRecord[]>();

        group.MapGet("/{jobName}", async Task<Result<KubernetesJobStatus>> (
            PipelineService pipelines,
            KubernetesClient kubernetesClient,
            KubernetesOptions options,
            Guid pipelineId,
            string jobName,
            CancellationToken ct) =>
        {
            var pipelineIdTyped = new Id<Pipeline>(new Id(pipelineId));

            if (!pipelines.TryGet(pipelineIdTyped, out _))
                return Result.Failure<KubernetesJobStatus>(new ResultProblem($"Pipeline '{pipelineId}' not found."));

            var status = await kubernetesClient.GetJobStatusAsync(options.Namespace, jobName, ct);
            return Result.Success(status);
        })
        .WithResultMapping<KubernetesJobStatus>();

        group.MapGet("/{jobName}/logs", async Task<Result<string>> (
            PipelineService pipelines,
            KubernetesClient kubernetesClient,
            KubernetesOptions options,
            Guid pipelineId,
            string jobName,
            CancellationToken ct) =>
        {
            var pipelineIdTyped = new Id<Pipeline>(new Id(pipelineId));

            if (!pipelines.TryGet(pipelineIdTyped, out _))
                return Result.Failure<string>(new ResultProblem($"Pipeline '{pipelineId}' not found."));

            var logs = await kubernetesClient.GetPodLogsAsync(options.Namespace, jobName, ct);
            return Result.Success(logs ?? "");
        })
        .WithResultMapping<string>();
    }
}
