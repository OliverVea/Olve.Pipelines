using Olve.Pipelines.Api;

namespace Olve.Pipelines.RealSystemTests.Chaos;

/// <summary>Deletes a running step's pod, gracefully or by force as an eviction from a lost node would. The control deletes nothing.</summary>
public class PodDeletionTests
{
    private const string DeletedPodReason = "Job failed (BackoffLimitExceeded): Job has reached the specified backoff limit";

    [Test, Explicit, ExclusiveServer(Fresh = true)]
    [MethodDataSource(typeof(Chaos), nameof(Chaos.Seeds))]
    public async Task Run_EndsWithAReason_WhenAPodIsDeleted(int seed)
    {
        var random = new Random(seed);
        var step = random.Next(2) == 0 ? "produce" : "consume";
        var delay = TimeSpan.FromMilliseconds(random.Next(3000));
        var action = Chaos.Control ? HostFault.None : HostFault.DeletePods(step, force: random.Next(2) == 0);
        var description = $"{action}, seed {seed}: {delay.TotalMilliseconds:0} ms after its Job was created";
        Console.WriteLine($"chaos {description}");

        await using var server = await TestServer.LeaseAsync();
        var pipeline = await Chaos.NewPipelineAsync(server, "chaos");

        var done = await server.ArmFaultAsync(action, $"Created K8s Job '[^']+' for step '{step}'", delay, TimeSpan.Zero);
        var acknowledged = await Chaos.TryStartRunAsync(server, pipeline);
        await done;

        var (run, violations) = await RunInvariants.CheckAsync(server, pipeline, acknowledged, mustSucceed: false);
        var failed = run?.Stages.SelectMany(s => s.Steps).Where(s => s.Status == RunStepStatus.Failed).ToList() ?? [];
        Chaos.FailOn([.. violations, .. failed.Where(s => s.Reason != DeletedPodReason).Select(s => $"step {s.Name} failed saying '{s.Reason}', expected '{DeletedPodReason}'")], description, seed);
    }
}
