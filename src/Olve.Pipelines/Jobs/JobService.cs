using System.Diagnostics.CodeAnalysis;
using Olve.Pipelines.Building;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Processing;
using Olve.Pipelines.Shared;
using Olve.Pipelines.Sourcing;
using Olve.Results;
using Olve.Utilities.Ids;
using static Olve.Pipelines.Jobs.Job;
using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.Jobs;

public class JobService(ILogger<JobService> logger, EntityStore<Job> store, IdProvider idProvider, TimeProvider timeProvider, JobEvents events)
{
    public IReadOnlyList<Job> ListJobs() => store.List();

    public Result<Job> CreateSourcingJob(Id<Pipeline> pipelineId)
    {
        SourcingJob job = new(idProvider.Create<Job>(), pipelineId, timeProvider.GetUtcNow(), new Scheduled());
        store.Set(job);
        events.OnAdded.Invoke(job.Id);
        return job;
    }

    public Result<Job> CreateBuildJob(Id<Pipeline> pipelineId, Id<SourceBundle> sourceBundleId)
    {
        BuildJob job = new(idProvider.Create<Job>(), pipelineId, timeProvider.GetUtcNow(), new Scheduled(), sourceBundleId);
        store.Set(job);
        events.OnAdded.Invoke(job.Id);
        return job;
    }

    public Result<Job> CreateProcessingJob(Id<Pipeline> pipelineId, Id<ArtifactBundle> artifactBundleId, Id<ProcessingStep> processingStepId)
    {
        ProcessingJob job = new(idProvider.Create<Job>(), pipelineId, timeProvider.GetUtcNow(), new Scheduled(), artifactBundleId, processingStepId);
        store.Set(job);
        events.OnAdded.Invoke(job.Id);
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
        events.OnUpdated.Invoke(jobId);
        return Result.Success();
    }

    public DeletionResult DeleteJob(Id<Job> jobId)
    {
        if (!store.Delete(jobId))
        {
            return DeletionResult.NotFound();
        }

        events.OnDeleted.Invoke(jobId);
        return DeletionResult.Success();
    }
}
