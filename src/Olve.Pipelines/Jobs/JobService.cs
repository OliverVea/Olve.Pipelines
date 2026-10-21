using System.Diagnostics.CodeAnalysis;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;
using Olve.Utilities.Paginations;
using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.Jobs;

public class JobService(ILogger<JobService> logger, EntityStore<Job> store, IdProvider idProvider, TimeProvider timeProvider)
{
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

    /// <summary>Every job of one pipeline (any status, unordered).</summary>
    public IReadOnlyList<Job> GetJobsByPipeline(Id<Pipeline> pipelineId)
    {
        var ids = _byPipeline.GetForKey(pipelineId);
        var results = new List<Job>(ids.Count);
        foreach (var id in ids)
        {
            if (store.TryGet(id, out var job))
                results.Add(job);
        }
        return results;
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

    /// <summary>Schedules a placeholder job running one named step of a pipeline.</summary>
    public Result<Job> CreateJob(
        Id<Pipeline> pipelineId, string stepName, string image, string script,
        Dictionary<string, string>? environmentVariables = null)
    {
        if (string.IsNullOrWhiteSpace(stepName))
            return new ResultProblem("Step name is required.");

        Job job = new(idProvider.Create<Job>(), pipelineId, stepName, image, script,
            timeProvider.GetUtcNow(), new Scheduled(), environmentVariables);
        store.Set(job);
        return job;
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
