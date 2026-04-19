using System.Collections.Concurrent;
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
    public const string AlreadyInProgressTag = "job.already_in_progress";

    private readonly EntityStoreIndex<Job, Id<JobGroup>> _byGroup = store.CreateIndex(j => j.JobGroupId);
    private readonly EntityStoreIndex<Job, Id<Pipeline>> _byPipeline = store.CreateIndex(j => j.PipelineId);

    private readonly ConcurrentDictionary<object, object> _keyLocks = new();

    private object GetKeyLock(object key) => _keyLocks.GetOrAdd(key, static _ => new object());

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
        var key = new ProductionJob.ProductionJobKey(pipelineId, productionStepId);
        lock (GetKeyLock(key))
        {
            if (TryFindInProgress(pipelineId, j => j is ProductionJob p && p.JobKey == key, out var existingId))
            {
                logger.LogInformation(
                    "Rejected ProductionJob creation for pipeline '{PipelineId}' step '{StepId}': job '{ExistingJobId}' is already InProgress",
                    pipelineId, productionStepId, existingId);
                return AlreadyInProgress("production", pipelineId, productionStepId.ToString(), existingId);
            }

            ProductionJob job = new(idProvider.Create<Job>(), pipelineId, timeProvider.GetUtcNow(), new Scheduled(), jobGroupId, productionStepId);
            store.Set(job);
            return job;
        }
    }

    public Result<Job> CreateProcessingJob(Id<Pipeline> pipelineId, Id<JobGroup> jobGroupId, Id<ArtifactBundle> artifactBundleId, Id<ProcessingStep> processingStepId)
    {
        var key = new ProcessingJob.ProcessingJobKey(pipelineId, processingStepId);
        lock (GetKeyLock(key))
        {
            if (TryFindInProgress(pipelineId, j => j is ProcessingJob p && p.JobKey == key, out var existingId))
            {
                logger.LogInformation(
                    "Rejected ProcessingJob creation for pipeline '{PipelineId}' step '{StepId}': job '{ExistingJobId}' is already InProgress",
                    pipelineId, processingStepId, existingId);
                return AlreadyInProgress("processing", pipelineId, processingStepId.ToString(), existingId);
            }

            ProcessingJob job = new(idProvider.Create<Job>(), pipelineId, timeProvider.GetUtcNow(), new Scheduled(), jobGroupId, artifactBundleId, processingStepId);
            store.Set(job);
            return job;
        }
    }

    private bool TryFindInProgress(Id<Pipeline> pipelineId, Func<Job, bool> predicate, out Id<Job> existingId)
    {
        foreach (var id in _byPipeline.GetForKey(pipelineId))
        {
            if (!store.TryGet(id, out var job)) continue;
            if (job.Status is not InProgress) continue;
            if (!predicate(job)) continue;

            existingId = id;
            return true;
        }

        existingId = default;
        return false;
    }

    private static ResultProblem AlreadyInProgress(string kind, Id<Pipeline> pipelineId, string stepId, Id<Job> existingJobId) =>
        new ResultProblem(
            "A {0} job is already in progress for pipeline '{1}' step '{2}' (job '{3}').",
            kind, pipelineId, stepId, existingJobId)
        {
            Tags = [AlreadyInProgressTag],
        };

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
