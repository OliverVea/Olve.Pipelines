using System.Text.Json.Nodes;
using Olve.Pipelines.Api;

namespace Olve.Pipelines.RealSystemTests.Chaos;

/// <summary>
/// Kills the server at a known moment of a run; the run must still finish, with one attempt and one Job per
/// step. Run by <c>tools/real-system/host.sh chaos</c>. <c>CHAOS_SEEDS</c> replays seeds, otherwise each
/// session walks every moment <c>CHAOS_ROUNDS</c> times (default 1) from a random seed.
/// </summary>
public class ServerCrashTests
{
    private const string Message = "survives the crash";
    private static readonly TimeSpan JobSettleBudget = TimeSpan.FromSeconds(30);

    private static readonly Lazy<IReadOnlyList<int>> SessionSeeds = new(() =>
    {
        if (Environment.GetEnvironmentVariable("CHAOS_SEEDS") is { Length: > 0 } listed)
            return listed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToList();

        var rounds = int.TryParse(Environment.GetEnvironmentVariable("CHAOS_ROUNDS"), out var n) ? n : 1;
        var start = Random.Shared.Next(1_000_000);
        return Enumerable.Range(start, rounds * ServerCrash.Moments.Count).ToList();
    });

    public static IEnumerable<int> Seeds() => SessionSeeds.Value;

    [Test, Explicit, ExclusiveServer(Fresh = true)]
    [MethodDataSource(nameof(Seeds))]
    public async Task Run_SurvivesAServerKill(int seed)
    {
        var crash = ServerCrash.FromSeed(seed);
        var graceful = Environment.GetEnvironmentVariable("CHAOS_GRACEFUL") == "1";
        Console.WriteLine($"chaos {crash}{(graceful ? " (graceful stop)" : "")}");

        await using var server = await TestServer.LeaseAsync();
        var pipeline = await server.NewPipelineAsync("chaos");
        await server.Api.SetSecretAsync(pipeline.Id, "FIXTURE_SECRET", "s3cret");
        await WaitUntilPersistedAsync(server, pipeline);

        var killed = await server.ArmServerCrashAsync(crash.Moment.LogLine, crash.Delay, crash.Downtime, graceful);
        var acknowledged = await TryStartRunAsync(server, pipeline);
        await killed;

        var violations = await CheckInvariantsAsync(server, pipeline, acknowledged);
        if (violations.Count > 0)
            Assert.Fail($"{crash}{(graceful ? " (graceful stop)" : "")}\n- {string.Join("\n- ", violations)}\nReplay: CHAOS_SEEDS={seed} tools/real-system/host.sh chaos <slot>");
    }

    /// <summary>What is lost must be the run's own state, not the pipeline it runs on.</summary>
    private static async Task WaitUntilPersistedAsync(TestServer server, TestPipeline pipeline)
    {
        var id = pipeline.Id.ToString();
        string[] keys = ["configuration.json", "pipeline-versions.json", "pipeline-state.json"];
        await Wait.UntilAsync(
            async () => (await Task.WhenAll(keys.Select(server.Bucket.ReadAsync))).Count(text => text?.Contains(id, StringComparison.OrdinalIgnoreCase) == true),
            found => found == keys.Length, Wait.Cluster, $"{pipeline.Name} to reach {string.Join(", ", keys)}");
    }

    /// <summary>The kill may beat the reply; then the run was never promised to anyone.</summary>
    private static async Task<bool> TryStartRunAsync(TestServer server, TestPipeline pipeline)
    {
        try
        {
            await server.StartRunAsync(pipeline, new JsonObject { ["message"] = Message });
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private static async Task<IReadOnlyList<string>> CheckInvariantsAsync(TestServer server, TestPipeline pipeline, bool acknowledged)
    {
        const int number = 1;
        var violations = new List<string>();
        var jobsSeen = new HashSet<string>();

        // Retention and the Job TTL remove orphans in time, so the check is over everything seen, not the last list.
        var run = await Wait.TryUntilAsync(async () =>
        {
            jobsSeen.UnionWith((await server.Cluster.StepJobsOfAsync(pipeline.Id)).Select(j => j.Name));
            return await server.Api.FindRunAsync(pipeline.Id, number);
        }, r => r?.Status != RunStatus.Running, Wait.Run);

        if (run is null)
        {
            if (acknowledged)
                violations.Add($"run #{number} was acknowledged and is gone after the restart");
            violations.AddRange(jobsSeen.Select(j => $"Job {j} belongs to no attempt: its run is gone"));
            return violations;
        }

        var attempts = await server.Api.ListAttemptsAsync(pipeline.Id, number);
        var steps = string.Join(", ", run.Stages.SelectMany(s => s.Steps).Select(s => $"{s.Name} {s.Status} ({s.AttemptCount} attempts): {s.Reason}"));
        if (run.Status != RunStatus.Succeeded)
            violations.Add($"run #{number} ended {run.Status}, expected Succeeded. Steps: {steps}");

        foreach (var step in attempts.GroupBy(a => a.StepName).Where(g => g.Count() > 1))
            violations.Add($"step {step.Key} has {step.Count()} attempts: {string.Join(", ", step.Select(a => $"{a.Number} {a.Status}"))}");

        var known = attempts.Select(a => $"olve-{a.Id:N}").ToHashSet();
        violations.AddRange(jobsSeen.Except(known).Select(j => $"Job {j} belongs to no attempt of the run"));

        var unfinished = await Wait.TryUntilAsync(
            async () => (await server.Cluster.StepJobsOfAsync(pipeline.Id)).Where(j => !j.Finished).Select(j => j.Name).ToList(),
            jobs => jobs.Count == 0, JobSettleBudget);
        violations.AddRange(unfinished.Select(j => $"Job {j} still runs after the run ended"));

        if (run.Status == RunStatus.Succeeded && !(await server.Api.GetStepOutputAsync(pipeline.Id, number, "consume")).Contains(Message, StringComparison.Ordinal))
            violations.Add($"consume's output lacks the run's message '{Message}'");

        return violations;
    }
}
