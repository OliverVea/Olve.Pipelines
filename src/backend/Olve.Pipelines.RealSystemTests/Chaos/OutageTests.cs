using Olve.Pipelines.Api;

namespace Olve.Pipelines.RealSystemTests.Chaos;

/// <summary>Takes a dependency of the server away for seconds at a known moment of a run. The control takes nothing away.</summary>
public class OutageTests
{
    private const int MinOutageMs = 2000;

    /// <summary>Step pods need Garage too, so a step may fail; the run must still end, say why, and stay saved.</summary>
    [Test, Explicit, ExclusiveServer(Fresh = true)]
    [MethodDataSource(typeof(Chaos), nameof(Chaos.Seeds))]
    public async Task Run_SurvivesAGarageOutage(int seed)
    {
        var fault = SeededFault.FromSeed(seed, MinOutageMs);
        var action = Chaos.Control ? HostFault.None : HostFault.StopGarage;
        var description = $"{action}, {fault}";
        Console.WriteLine($"chaos {description}");

        await using var server = await TestServer.LeaseAsync();
        var pipeline = await Chaos.NewPipelineAsync(server, "chaos");

        var done = await server.ArmFaultAsync(action, fault.Moment.LogLine, fault.Delay, fault.Downtime);
        var acknowledged = await Chaos.TryStartRunAsync(server, pipeline);
        await done;

        var (run, violations) = await RunInvariants.CheckAsync(server, pipeline, acknowledged, mustSucceed: false);
        var list = violations.ToList();
        if (run is not null && run.Status != RunStatus.Running)
        {
            var restarted = await server.ArmFaultAsync(HostFault.StopServer, afterLogLine: null, TimeSpan.Zero, TimeSpan.Zero);
            await restarted;
            var reloaded = await server.Api.FindRunAsync(pipeline.Id, run.Number);
            if (reloaded?.Status != run.Status)
                list.Add($"run #{run.Number} ended {run.Status} and came back from a graceful restart as {reloaded?.Status.ToString() ?? "nothing"}: the save never landed");
        }
        Chaos.FailOn(list, description, seed);
    }

    [Test, Explicit, ExclusiveServer(Fresh = true)]
    [MethodDataSource(typeof(Chaos), nameof(Chaos.Seeds))]
    public async Task Run_SurvivesAKubernetesApiOutage(int seed)
    {
        var fault = SeededFault.FromSeed(seed, MinOutageMs);
        var action = Chaos.Control ? HostFault.None : HostFault.PauseKubernetes;
        var description = $"{action}, {fault}";
        Console.WriteLine($"chaos {description}");

        await using var server = await TestServer.LeaseAsync();
        var pipeline = await Chaos.NewPipelineAsync(server, "chaos");

        var done = await server.ArmFaultAsync(action, fault.Moment.LogLine, fault.Delay, fault.Downtime);
        var acknowledged = await Chaos.TryStartRunAsync(server, pipeline);
        await done;

        var (_, violations) = await RunInvariants.CheckAsync(server, pipeline, acknowledged, mustSucceed: true);
        Chaos.FailOn(violations, description, seed);
    }
}
