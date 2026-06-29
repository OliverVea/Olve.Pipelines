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

public class JobService(ILogger<JobService> logger, EntityStore<Job> store, JobGroupService jobGroups, IdProvider idProvider, TimeProvider timeProvider)
{
    private readonly EntityStoreIndex<Job, Id<JobGroup>> _byGroup = store.CreateIndex(j => j.JobGroupId);
    private readonly EntityStoreIndex<Job, Id<Pipeline>> _byPipeline = store.CreateIndex(j => j.PipelineId);

    public IReadOnlyList<Job> ListJobs() => store.List();

    /// <summary>
    /// True while the pipeline has any non-terminal job (<see cref="Scheduled"/> or
    /// <see cref="InProgress"/>). The reconcile drain waits for this to go false (quiescence,
    /// not success — a failed/cancelled job is terminal).
    /// </summary>
    public bool HasActiveJobs(Id<Pipeline> pipelineId)
    {
        foreach (var id in _byPipeline.GetForKey(pipelineId))
        {
            if (store.TryGet(id, out var job) && job.Status is Scheduled or InProgress)
                return true;
        }

        return false;
    }

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

    /// <summary>
    /// Creates a processing run for a (step, bundle): a <see cref="ProcessingJobGroup"/> and its
    /// scheduled <see cref="ProcessingJob"/>. The single entry point every promotion path funnels
    /// through — the automatic cascade, the manual trigger, the trigger-entity path, and re-promote —
    /// so the group+job pair is created identically everywhere.
    /// </summary>
    public ProcessingJobGroup CreateProcessingRun(Id<Pipeline> pipelineId, Id<ArtifactBundle> artifactBundleId, Id<ProcessingStep> processingStepId)
    {
        var jobGroup = jobGroups.CreateProcessingGroup(pipelineId, artifactBundleId, processingStepId);
        _ = CreateProcessingJob(pipelineId, jobGroup.Id, artifactBundleId, processingStepId); // store-only create, cannot fail
        return jobGroup;
    }

    /// <summary>
    /// The artifact bundle most recently promoted into <paramref name="processingStepId"/>, i.e. the
    /// bundle of the newest <see cref="ProcessingJob"/> created for that step. Used by re-promote to
    /// redrive the same bundle. Null when the step has never run.
    /// </summary>
    public Id<ArtifactBundle>? GetLastPromotedBundle(Id<Pipeline> pipelineId, Id<ProcessingStep> processingStepId)
    {
        ProcessingJob? latest = null;
        foreach (var id in _byPipeline.GetForKey(pipelineId))
        {
            if (store.TryGet(id, out var job)
                && job is ProcessingJob processing
                && processing.ProcessingStepId == processingStepId
                && (latest is null || processing.CreatedAt > latest.CreatedAt))
            {
                latest = processing;
            }
        }

        return latest?.ArtifactBundleId;
    }

    /// <summary>
    /// The most recent job for each (production/processing) step in a pipeline, keyed by step Id.
    /// One pass over the pipeline's jobs; used by the list summary to colour the step-health strip
    /// without an N+1 of per-step queries.
    /// </summary>
    public (Dictionary<Id<ProductionStep>, Job> Production, Dictionary<Id<ProcessingStep>, Job> Processing)
        GetLatestJobsByStep(Id<Pipeline> pipelineId)
    {
        var production = new Dictionary<Id<ProductionStep>, Job>();
        var processing = new Dictionary<Id<ProcessingStep>, Job>();

        foreach (var id in _byPipeline.GetForKey(pipelineId))
        {
            if (!store.TryGet(id, out var job))
                continue;

            switch (job)
            {
                case ProductionJob p
                    when !production.TryGetValue(p.ProductionStepId, out var cur) || p.CreatedAt > cur.CreatedAt:
                    production[p.ProductionStepId] = p;
                    break;
                case ProcessingJob p
                    when !processing.TryGetValue(p.ProcessingStepId, out var cur) || p.CreatedAt > cur.CreatedAt:
                    processing[p.ProcessingStepId] = p;
                    break;
            }
        }

        return (production, processing);
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
        // Advisory type/existence check for the typed contract; the atomic write is the Mutate below.
        if (!TryGetJob<T>(jobId, out _))
        {
            return new ResultProblem("Job with id '{0}' not found.", jobId);
        }

        // Mutate runs under a CAS retry loop, so the guard keeps it pure for non-T values.
        return store.Mutate(jobId, j => j is T typed ? update(typed) : j);
    }

    public Result CancelJob(Id<Job> jobId)
    {
        if (!store.TryGet(jobId, out var job))
        {
            return new ResultProblem("Job with id '{0}' not found.", jobId);
        }

        // Advisory pre-check for the can't-cancel message; the write happens atomically in Mutate,
        // whose lambda re-derives the cancelled state (pure function of the current job).
        if (job.Status is not (Scheduled or InProgress))
        {
            return new ResultProblem("Job with id '{0}' cannot be cancelled because it is {1}.", jobId, job.Status.GetType().Name);
        }

        return store.Mutate(jobId, j => j.Status switch
        {
            Scheduled => j with { Status = new Cancelled(null, timeProvider.GetUtcNow()) },
            InProgress inProgress => j with { Status = new Cancelled(inProgress.StartedAt, timeProvider.GetUtcNow()) },
            _ => j,
        });
    }

    public DeletionResult DeleteJob(Id<Job> jobId) => store.Delete(jobId);
}
