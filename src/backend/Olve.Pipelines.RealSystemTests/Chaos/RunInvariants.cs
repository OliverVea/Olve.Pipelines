using Olve.Pipelines.Api;

namespace Olve.Pipelines.RealSystemTests.Chaos;

/// <summary>The run after a fault: it finishes, each step has one attempt, and every Job belongs to an attempt.</summary>
public static class RunInvariants
{
    private const int Number = 1;
    private static readonly TimeSpan JobSettleBudget = TimeSpan.FromSeconds(30);

    /// <param name="mustSucceed">The fault leaves the run nothing to fail on. Otherwise a failed step must say why.</param>
    public static async Task<(Run? Run, IReadOnlyList<string> Violations)> CheckAsync(
        TestServer server, TestPipeline pipeline, bool acknowledged, bool mustSucceed)
    {
        var violations = new List<string>();
        var jobsSeen = new HashSet<string>();

        // Retention and the Job TTL remove orphans in time, so the check is over everything seen, not the last list.
        var run = await Wait.TryUntilAsync(async () =>
        {
            jobsSeen.UnionWith((await server.Cluster.StepJobsOfAsync(pipeline.Id)).Select(j => j.Name));
            return await server.Api.FindRunAsync(pipeline.Id, Number);
        }, r => r?.Status != RunStatus.Running, Wait.Run);

        if (run is null)
        {
            if (acknowledged)
                violations.Add($"run #{Number} was acknowledged and is gone after the restart");
            violations.AddRange(jobsSeen.Select(j => $"Job {j} belongs to no attempt: its run is gone"));
            return (null, violations);
        }

        var attempts = await server.Api.ListAttemptsAsync(pipeline.Id, Number);
        var stepViews = run.Stages.SelectMany(s => s.Steps).ToList();
        var steps = string.Join(", ", stepViews.Select(s => $"{s.Name} {s.Status} ({s.AttemptCount} attempts): {s.Reason}"));
        Console.WriteLine($"chaos outcome: run #{Number} {run.Status}. Steps: {steps}");
        if (run.Status == RunStatus.Running)
            violations.Add($"run #{Number} did not finish within {Wait.Run.TotalSeconds:0}s. Steps: {steps}");
        else if (mustSucceed && run.Status != RunStatus.Succeeded)
            violations.Add($"run #{Number} ended {run.Status}, expected Succeeded. Steps: {steps}");
        else if (run.Status != RunStatus.Succeeded && stepViews.All(s => string.IsNullOrWhiteSpace(s.Reason)))
            violations.Add($"run #{Number} ended {run.Status} and no step says why. Steps: {steps}");

        foreach (var step in attempts.GroupBy(a => a.StepName).Where(g => g.Count() > 1))
            violations.Add($"step {step.Key} has {step.Count()} attempts: {string.Join(", ", step.Select(a => $"{a.Number} {a.Status}"))}");

        var known = attempts.Select(a => $"olve-{a.Id:N}").ToHashSet();
        violations.AddRange(jobsSeen.Except(known).Select(j => $"Job {j} belongs to no attempt of the run"));

        var unfinished = await Wait.TryUntilAsync(
            async () => (await server.Cluster.StepJobsOfAsync(pipeline.Id)).Where(j => !j.Finished).Select(j => j.Name).ToList(),
            jobs => jobs.Count == 0, JobSettleBudget);
        violations.AddRange(unfinished.Select(j => $"Job {j} still runs after the run ended"));

        if (run.Status == RunStatus.Succeeded && !(await server.Api.GetStepOutputAsync(pipeline.Id, Number, "consume")).Contains(Chaos.Message, StringComparison.Ordinal))
            violations.Add($"consume's output lacks the run's message '{Chaos.Message}'");

        return (run, violations);
    }
}
