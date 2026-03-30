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

public class JobService(ILogger<JobService> logger, EntityStore<Job> store, IdProvider idProvider, TimeProvider timeProvider)
{
    private Id<Job> GetNewJobId() => idProvider.Create<Job>();
    private DateTimeOffset GetCreationTime() => timeProvider.GetUtcNow();
    private JobStatus GetJobStatus() => new Scheduled();

    private readonly Dictionary<SourcingJob.Key, Id<Job>> _pendingSourcingJobs = new();
    private readonly Dictionary<BuildJob.Key, Id<Job>> _pendingBuildJobs = new();
    private readonly Dictionary<ProcessingJob.Key, Id<Job>> _pendingProcessingJobs = new();

    public Result<Job> CreateSourcingJob(Id<Pipeline> pipelineId)
    {
        SourcingJob job = new(GetNewJobId(), pipelineId, GetCreationTime(), GetJobStatus());
        ObsoleteExistingJobs<SourcingJob, SourcingJob.Key>(job.Id, job.JobKey, _pendingSourcingJobs);
        store.Set(job);
        return job;
    }

    public Result<Job> CreateBuildJob(Id<Pipeline> pipelineId, Id<SourceBundle> sourceBundleId)
    {
        BuildJob job = new(GetNewJobId(), pipelineId, GetCreationTime(), GetJobStatus(), sourceBundleId);
        ObsoleteExistingJobs<BuildJob, BuildJob.Key>(job.Id, job.JobKey, _pendingBuildJobs);
        store.Set(job);
        return job;
    }

    public Result<Job> CreateProcessingJob(Id<Pipeline> pipelineId, Id<ArtifactBundle> artifactBundleId, Id<ProcessingStep> processingStepId)
    {
        ProcessingJob job = new(GetNewJobId(), pipelineId, GetCreationTime(), GetJobStatus(), artifactBundleId, processingStepId);
        ObsoleteExistingJobs<ProcessingJob, ProcessingJob.Key>(job.Id, job.JobKey, _pendingProcessingJobs);
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

    public DeletionResult DeleteJob(Id<Job> jobId)
    {
        if (!store.Delete(jobId))
        {
            return DeletionResult.NotFound();
        }

        // TODO: clean up pending dictionaries.

        return DeletionResult.Success();
    }

    private void ObsoleteExistingJobs<TJob, TJobKey>(Id<Job> newJobId, TJobKey jobKey, Dictionary<TJobKey, Id<Job>> pendingJobs)
        where TJob : Job where TJobKey : notnull
    {
        lock (pendingJobs)
        {
            if (pendingJobs.TryGetValue(jobKey, out var existingJobId))
            {
                var updateResult = UpdateJob<TJob>(existingJobId, j => j with { Status = new Obsolete(newJobId) });
                if (updateResult.TryPickProblems(out var problems))
                {
                    logger.LogWarning("Failed to cancel previous job with id '{ExistingJobId}' and key '{JobKey}' while scheduling new job with id '{NewJobId}': {Problems}", existingJobId, jobKey, newJobId, problems);
                }
            }

            pendingJobs[jobKey] = newJobId;
        }
    }
}


