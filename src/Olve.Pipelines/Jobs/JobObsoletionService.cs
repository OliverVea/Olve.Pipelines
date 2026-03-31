using Olve.Utilities.Ids;
using static Olve.Pipelines.Jobs.Job;
using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.Jobs;

public class JobObsoletionService
{
    private readonly JobService _jobService;
    private readonly ILogger<JobObsoletionService> _logger;

    public JobObsoletionService(JobService jobService, ILogger<JobObsoletionService> logger)
    {
        _jobService = jobService;
        _logger = logger;

        jobService.OnJobAdded += OnJobAdded;
    }

    private void OnJobAdded(Id<Job> jobId)
    {
        if (!_jobService.TryGetJob<Job>(jobId, out var newJob))
            return;

        var existingJob = _jobService.ListJobs()
            .FirstOrDefault(j => j.Id != newJob.Id && j.Status is Scheduled && HasSameKey(j, newJob));

        if (existingJob is null)
            return;

        var result = _jobService.UpdateJob<Job>(existingJob.Id, j => j with { Status = new Obsolete(newJob.Id) });
        if (result.TryPickProblems(out var problems))
        {
            _logger.LogWarning(
                "Failed to obsolete job '{ExistingJobId}' when superseded by '{NewJobId}': {Problems}",
                existingJob.Id, newJob.Id, problems);
        }
    }

    private static bool HasSameKey(Job a, Job b) => (a, b) switch
    {
        (SourcingJob sa, SourcingJob sb) => sa.JobKey == sb.JobKey,
        (BuildJob ba, BuildJob bb) => ba.JobKey == bb.JobKey,
        (ProcessingJob pa, ProcessingJob pb) => pa.JobKey == pb.JobKey,
        _ => false
    };
}
