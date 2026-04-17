using System.Diagnostics.CodeAnalysis;
using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Shared;
using Olve.Utilities.Paginations;
using static Olve.Pipelines.Jobs.Job;
using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.Jobs;

public class JobService(ILogger<JobService> logger, EntityStore<Job> store, IdProvider idProvider, TimeProvider timeProvider)
{
    private readonly EntityStoreIndex<Job, Id<JobGroup>> _byGroup = store.CreateIndex(j => j.JobGroupId);
    private readonly EntityStoreIndex<Job, Id<Pipeline>> _byPipeline = store.CreateIndex(j => j.PipelineId);

    public IReadOnlyList<Job> ListJobs() => store.List();

    public Page<Job> ListJobs(ListJobsRequest request)
    {
        IEnumerable<Job> source;
        if (request.PipelineId is { } pipelineId)
        {
            var ids = _byPipeline.GetForKey(pipelineId);
            var list = new List<Job>(ids.Count);
            foreach (var id in ids)
            {
                if (store.TryGet(id, out var job))
                    list.Add(job);
            }
            source = list;
        }
        else
        {
            source = store.List();
        }

        var sorted = request.Sort switch
        {
            JobSortField.CreatedAtAsc => source.OrderBy(j => j.CreatedAt),
            _ => source.OrderByDescending(j => j.CreatedAt),
        };

        var materialized = sorted.ToList();
        var pageNumber = Math.Max(0, request.Page);
        var pageSize = Math.Max(1, request.PageSize);
        var items = materialized
            .Skip(pageNumber * pageSize)
            .Take(pageSize)
            .ToList();

        return new Page<Job>(items, pageNumber, pageSize, materialized.Count);
    }

    public Result<Job> CreateProductionJob(Id<Pipeline> pipelineId, Id<JobGroup> jobGroupId, Id<ProductionStep> productionStepId)
    {
        ProductionJob job = new(idProvider.Create<Job>(), pipelineId, timeProvider.GetUtcNow(), new Scheduled(), jobGroupId, productionStepId);
        store.Set(job);
        return job;
    }

    public Result<Job> CreateProcessingJob(Id<Pipeline> pipelineId, Id<JobGroup> jobGroupId, Id<ArtifactBundle> artifactBundleId, Id<ProcessingStep> processingStepId)
    {
        ProcessingJob job = new(idProvider.Create<Job>(), pipelineId, timeProvider.GetUtcNow(), new Scheduled(), jobGroupId, artifactBundleId, processingStepId);
        store.Set(job);
        return job;
    }

    public IReadOnlyList<Job> GetJobsByGroup(Id<JobGroup> jobGroupId)
    {
        var ids = _byGroup.GetForKey(jobGroupId);
        var results = new List<Job>(ids.Count);
        foreach (var id in ids)
        {
            if (store.TryGet(id, out var job))
                results.Add(job);
        }
        return results;
    }

    public bool TryGetJob<T>(Id<Job> jobId, [MaybeNullWhen(false)] out T job)
        where T : Job
    {
        if (!store.TryGet(jobId, out var genericJob))
        {
            job = null;
            return false;
        }

        if (genericJob is not T typedJob)
        {
            logger.LogWarning("Job with id '{JobId}' was not expected type '{ExpectedType}' but rather '{ActualType}'",
                jobId,
                typeof(T).Name,
                genericJob.GetType().Name);

            job = null;
            return false;
        }

        job = typedJob;
        return true;
    }

    public Result UpdateJob<T>(Id<Job> jobId, Func<T, T> update) where T : Job
    {
        if (!TryGetJob<T>(jobId, out var currentJob))
        {
            return new ResultProblem("Job with id '{0}' not found.", jobId);
        }

        var updatedJob = update(currentJob);
        if (updatedJob == currentJob)
        {
            return Result.Success();
        }

        store.Set(updatedJob);
        return Result.Success();
    }

    public Result CancelJob(Id<Job> jobId)
    {
        if (!store.TryGet(jobId, out var job))
        {
            return new ResultProblem("Job with id '{0}' not found.", jobId);
        }

        var cancelled = job.Status switch
        {
            Scheduled => job with { Status = new Cancelled(null, timeProvider.GetUtcNow()) },
            InProgress inProgress => job with { Status = new Cancelled(inProgress.StartedAt, timeProvider.GetUtcNow()) },
            _ => (Job?)null,
        };

        if (cancelled is null)
        {
            return new ResultProblem("Job with id '{0}' cannot be cancelled because it is {1}.", jobId, job.Status.GetType().Name);
        }

        store.Set(cancelled);
        return Result.Success();
    }

    public DeletionResult DeleteJob(Id<Job> jobId) => store.Delete(jobId);
}
