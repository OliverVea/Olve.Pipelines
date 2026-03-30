using Olve.Pipelines.Shared;
using Olve.Utilities.Ids;
using static Olve.Pipelines.Jobs.Job;
using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.Jobs;

public class JobObsoletionService
{
    private readonly JobService _jobService;
    private readonly EntityStore<Job> _store;
    private readonly ILogger<JobObsoletionService> _logger;

    private readonly Dictionary<SourcingJob.Key, Id<Job>> _pendingSourcingJobs = new();
    private readonly Dictionary<BuildJob.Key, Id<Job>> _pendingBuildJobs = new();
    private readonly Dictionary<ProcessingJob.Key, Id<Job>> _pendingProcessingJobs = new();

    public JobObsoletionService(JobService jobService, EntityStore<Job> store, ILogger<JobObsoletionService> logger)
    {
        _jobService = jobService;
        _store = store;
        _logger = logger;

        store.OnAdded += OnJobAdded;
        store.OnUpdated += OnJobUpdated;
    }

    private void OnJobAdded(Id<Job> jobId)
    {
        if (!_store.TryGet(jobId, out var job))
            return;

        switch (job)
        {
            case SourcingJob sourcingJob:
                ObsoleteExisting<SourcingJob, SourcingJob.Key>(sourcingJob.Id, sourcingJob.JobKey, _pendingSourcingJobs);
                break;
            case BuildJob buildJob:
                ObsoleteExisting<BuildJob, BuildJob.Key>(buildJob.Id, buildJob.JobKey, _pendingBuildJobs);
                break;
            case ProcessingJob processingJob:
                ObsoleteExisting<ProcessingJob, ProcessingJob.Key>(processingJob.Id, processingJob.JobKey, _pendingProcessingJobs);
                break;
        }
    }

    private void OnJobUpdated(Id<Job> jobId)
    {
        if (!_store.TryGet(jobId, out var job))
            return;

        if (job.Status is Scheduled)
            return;

        switch (job)
        {
            case SourcingJob sourcingJob:
                RemoveFromPending(sourcingJob.JobKey, jobId, _pendingSourcingJobs);
                break;
            case BuildJob buildJob:
                RemoveFromPending(buildJob.JobKey, jobId, _pendingBuildJobs);
                break;
            case ProcessingJob processingJob:
                RemoveFromPending(processingJob.JobKey, jobId, _pendingProcessingJobs);
                break;
        }
    }

    private void ObsoleteExisting<TJob, TJobKey>(Id<Job> newJobId, TJobKey jobKey, Dictionary<TJobKey, Id<Job>> pendingJobs)
        where TJob : Job where TJobKey : notnull
    {
        lock (pendingJobs)
        {
            if (pendingJobs.TryGetValue(jobKey, out var existingJobId))
            {
                var updateResult = _jobService.UpdateJob<TJob>(existingJobId, j => j with { Status = new Obsolete(newJobId) });
                if (updateResult.TryPickProblems(out var problems))
                {
                    _logger.LogWarning(
                        "Failed to obsolete job '{ExistingJobId}' for key '{JobKey}' when superseded by '{NewJobId}': {Problems}",
                        existingJobId, jobKey, newJobId, problems);
                }
            }

            pendingJobs[jobKey] = newJobId;
        }
    }

    private static void RemoveFromPending<TJobKey>(TJobKey jobKey, Id<Job> jobId, Dictionary<TJobKey, Id<Job>> pendingJobs)
        where TJobKey : notnull
    {
        lock (pendingJobs)
        {
            if (pendingJobs.TryGetValue(jobKey, out var pendingId) && pendingId == jobId)
            {
                pendingJobs.Remove(jobKey);
            }
        }
    }
}
