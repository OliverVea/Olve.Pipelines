using static Olve.Pipelines.Jobs.Job;
using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.Jobs;

public class JobObsoletionService(JobService jobService, ILogger<JobObsoletionService> logger)
{
    public void HandleJobAdded(Id<Job> jobId)
    {
        if (!jobService.TryGetJob<Job>(jobId, out var newJob))
            return;

        // Every still-schedulable job sharing this (pipeline, step) key, newJob included.
        var contenders = jobService.ListJobs()
            .Where(j => j.Status is Scheduled && HasSameKey(j, newJob))
            .ToList();

        if (contenders.Count < 2)
            return;

        // Latest-wins as a STRICT total order: newest CreatedAt, with Id as a deterministic
        // tie-break for genuinely simultaneous creations. The winner is the maximum of a fixed
        // ordering — never "the other job" — so two jobs created concurrently can never name each
        // other as superseding (the mutual-supersession cycle that silently wedged the pipeline).
        // The maximum is never obsoleted, so after any burst of concurrent scheduling exactly one
        // runnable job survives for the key.
        var winner = contenders
            .OrderByDescending(j => j.CreatedAt)
            .ThenByDescending(j => j.Id)
            .First();

        foreach (var loser in contenders)
        {
            if (loser.Id == winner.Id)
                continue;

            // Re-check Scheduled inside the update: a contender may have advanced to InProgress
            // since the snapshot (never clobber a running job), and re-obsoleting to the same winner
            // is idempotent under concurrent handlers.
            var result = jobService.UpdateJob<Job>(loser.Id, j =>
                j.Status is Scheduled ? j with { Status = new Obsolete(winner.Id) } : j);

            if (result.TryPickProblems(out var problems))
            {
                logger.LogProblems(LogLevel.Warning, problems,
                    "Failed to obsolete job '{LoserJobId}' when superseded by '{WinnerJobId}'",
                    loser.Id, winner.Id);
            }
        }
    }

    private static bool HasSameKey(Job a, Job b) => (a, b) switch
    {
        (ProductionJob pa, ProductionJob pb) => pa.JobKey == pb.JobKey,
        (ProcessingJob pa, ProcessingJob pb) => pa.JobKey == pb.JobKey,
        _ => false
    };
}
