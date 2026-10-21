using Olve.Pipelines.Pipelines.Runs;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.Jobs;

/// <summary>Latest-wins among Scheduled attempts sharing a (pipeline, step name) key (spec §5.3).</summary>
public class JobObsoletionService(AttemptService attempts, ILogger<JobObsoletionService> logger)
{
    public void HandleAttemptAdded(Id<Attempt> attemptId)
    {
        if (!attempts.TryGet(attemptId, out var newAttempt))
            return;

        // Every still-schedulable attempt sharing this (pipeline, step) key, newAttempt included.
        var contenders = attempts.ListByPipeline(newAttempt.PipelineId)
            .Where(a => a.Status is Scheduled && a.Key == newAttempt.Key)
            .ToList();

        if (contenders.Count < 2)
            return;

        // Latest-wins as a STRICT total order: newest CreatedAt, with Id as a deterministic
        // tie-break for genuinely simultaneous creations. The winner is the maximum of a fixed
        // ordering — never "the other attempt" — so two attempts created concurrently can never name
        // each other as superseding (the mutual-supersession cycle that silently wedged the pipeline).
        // The maximum is never obsoleted, so after any burst of concurrent scheduling exactly one
        // runnable attempt survives for the key.
        var winner = contenders
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .First();

        foreach (var loser in contenders)
        {
            if (loser.Id == winner.Id)
                continue;

            // Re-check Scheduled inside the update: a contender may have advanced to InProgress
            // since the snapshot (never clobber a running attempt), and re-obsoleting to the same
            // winner is idempotent under concurrent handlers.
            var result = attempts.Update(loser.Id, a =>
                a.Status is Scheduled ? a with { Status = new Obsolete(winner.Id) } : a);

            if (result.TryPickProblems(out var problems))
            {
                logger.LogProblems(LogLevel.Warning, problems,
                    "Failed to obsolete attempt '{LoserAttemptId}' of step '{StepName}' when superseded by '{WinnerAttemptId}'",
                    loser.Id, loser.StepName, winner.Id);
            }
        }
    }
}
