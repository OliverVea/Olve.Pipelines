namespace Olve.Pipelines.RealSystemTests.Chaos;

/// <summary>Kills the server at a known moment of a run. The control stops it gracefully at the same moment.</summary>
public class ServerCrashTests
{
    [Test, Explicit, ExclusiveServer(Fresh = true)]
    [MethodDataSource(typeof(Chaos), nameof(Chaos.Seeds))]
    public async Task Run_SurvivesAServerKill(int seed)
    {
        var fault = SeededFault.FromSeed(seed);
        var action = Chaos.Control ? HostFault.StopServer : HostFault.KillServer;
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
