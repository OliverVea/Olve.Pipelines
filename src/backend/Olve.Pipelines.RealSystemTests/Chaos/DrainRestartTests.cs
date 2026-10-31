using System.Net;
using System.Text.Json.Nodes;
using Olve.Pipelines.Api;

namespace Olve.Pipelines.RealSystemTests.Chaos;

/// <summary>
/// Restarts the server right after a <c>--cancel-running</c> switch cancelled a run in flight, before the
/// executor deleted its Job. The switch itself ends within the request, so the restart can't land inside it.
/// Seeds alternate between a graceful stop, as a self-deploy does, and a kill. The control restarts nothing.
/// </summary>
public class DrainRestartTests
{
    private const string PipelineFile = ".pipelines/pipeline.ts";

    /// <summary>Well under the host's retention grace, which would hide a leaked Job.</summary>
    private static readonly TimeSpan JobGoneBudget = TimeSpan.FromSeconds(20);

    [Test, Explicit, ExclusiveServer(Fresh = true)]
    [MethodDataSource(typeof(Chaos), nameof(Chaos.Seeds))]
    public async Task Switch_Completes_WhenTheServerRestartsDuringTheDrain(int seed)
    {
        var random = new Random(seed);
        var delay = TimeSpan.FromMilliseconds(random.Next(SeededFault.FlushWindowMs));
        var action = Chaos.Control ? HostFault.None : seed % 2 == 0 ? HostFault.StopServer : HostFault.KillServer;
        var description = $"{action}, seed {seed}: {delay.TotalMilliseconds:0} ms after the cancelling started";
        Console.WriteLine($"chaos {description}");

        await using var server = await TestServer.LeaseAsync();
        var pipeline = await Chaos.NewPipelineAsync(server, "drain");
        await CommitASecondVersionAsync(server, pipeline);

        var inFlight = await server.StartRunAsync(pipeline, new JsonObject { ["message"] = Chaos.Message });
        await Wait.UntilAsync(() => server.Cluster.StepJobsOfAsync(pipeline.Id), jobs => jobs.Count > 0, Wait.Cluster, "the run's first Job");
        await WaitUntilRunIsSavedInProgressAsync(server, pipeline, inFlight.Number);

        var done = await server.ArmFaultAsync(action, "Cancelling [0-9]+ in-flight attempt", delay, TimeSpan.Zero);
        var switched = await TryAsync(() => server.Api.SwitchAsync(pipeline.Id, version: 1, cancelRunning: true));
        var queued = await TryAsync(() => server.Api.RequestRunAsync(pipeline.Id, new JsonObject { ["message"] = "queued" }));
        await done;

        Console.WriteLine($"chaos outcome: switch {(switched is null ? "not acknowledged" : "acknowledged")}, second start {queued?.Outcome.ToString() ?? "not acknowledged"}");
        var violations = new List<string>();
        if (switched is not null)
            await CheckTheSwitchAsync(server, pipeline, inFlight.Number, violations);
        if (queued is not null)
            await CheckTheNewRunAsync(server, pipeline, inFlight.Number + 1, violations);
        Chaos.FailOn(violations, description, seed);
    }

    private static async Task WaitUntilRunIsSavedInProgressAsync(TestServer server, TestPipeline pipeline, int number)
    {
        var key = $"runs/{pipeline.Id:N}/{number}/run.json";
        await Wait.UntilAsync(() => server.Bucket.ReadAsync(key), text => text?.Contains("inprogress", StringComparison.OrdinalIgnoreCase) == true,
            Wait.Cluster, $"{key} to say the attempt is in progress");
    }

    /// <summary>The second version's first step outlasts the test, so only a deletion ends its Job.</summary>
    private static async Task CommitASecondVersionAsync(TestServer server, TestPipeline pipeline)
    {
        var fixture = await File.ReadAllTextAsync(Path.Combine(TestTargets.RepoRoot(), "tools/real-system/fixture", PipelineFile));
        await server.GitHub.CommitAsync(pipeline.Repo, new Dictionary<string, string>
        {
            [PipelineFile] = fixture.Replace("__PIPELINE_NAME__", pipeline.Name)
                .Replace("Real-system test fixture.", "The second version.")
                .Replace("writeFileSync(path, request.message);", "writeFileSync(path, request.message); await new Promise((done) => setTimeout(done, 100_000));"),
        });
        var reconcile = await server.Api.ReconcileAsync(pipeline.Id);
        if (reconcile.Status != HttpStatusCode.OK)
            throw new InvalidOperationException($"The second version did not reconcile: {reconcile.Detail}");
        var second = (await server.Api.ListVersionsAsync(pipeline.Id)).Versions.Single(v => v.Number == 2);
        await Chaos.WaitUntilPersistedAsync(server, second.Id.ToString());
    }

    private static async Task CheckTheSwitchAsync(TestServer server, TestPipeline pipeline, int number, List<string> violations)
    {
        var versions = await Wait.TryUntilAsync(() => server.Api.ListVersionsAsync(pipeline.Id), v => v.Activation is PipelineActivationActive { VersionNumber: 1 }, Wait.Run);
        if (versions.Activation is not PipelineActivationActive { VersionNumber: 1 })
            violations.Add($"the switch to version 1 was acknowledged; activation is {versions.Activation}");

        var run = await Wait.TryUntilAsync(() => server.Api.FindRunAsync(pipeline.Id, number), r => r?.Status != RunStatus.Running, Wait.Run);
        if (run?.Status != RunStatus.Cancelled)
            violations.Add($"run #{number} on the old version ended {run?.Status.ToString() ?? "gone"}, expected Cancelled");

        var running = await Wait.TryUntilAsync(
            async () => (await server.Cluster.StepJobsOfAsync(pipeline.Id)).Where(j => !j.Finished && j.Run == number).Select(j => j.Name).ToList(),
            jobs => jobs.Count == 0, JobGoneBudget);
        violations.AddRange(running.Select(j => $"Job {j} of the cancelled run still runs {JobGoneBudget.TotalSeconds:0}s after the restart"));
    }

    private static async Task CheckTheNewRunAsync(TestServer server, TestPipeline pipeline, int number, List<string> violations)
    {
        var run = await Wait.TryUntilAsync(() => server.Api.FindRunAsync(pipeline.Id, number), r => r is { Status: not RunStatus.Running }, Wait.Run);
        if (run is null)
            violations.Add($"the start acknowledged after the switch never became run #{number}");
        else if (run.Status != RunStatus.Succeeded || run.VersionNumber != 1)
            violations.Add($"run #{number} ended {run.Status} on version {run.VersionNumber}, expected Succeeded on version 1");
    }

    private static async Task<T?> TryAsync<T>(Func<Task<T>> call) where T : class
    {
        try
        {
            return await call();
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }
}
