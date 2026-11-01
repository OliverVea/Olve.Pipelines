namespace Olve.Pipelines.RealSystemTests.Flows;

public class HostGuardTests
{
    [Test]
    public async Task TheHost_IsMarkedAsATestCluster()
    {
        await using var server = await TestServer.LeaseAsync();

        await Assert.That(await server.Cluster.ReadMarkerAsync()).IsEqualTo(server.Target.ClusterId);
    }

    [Test]
    public async Task AClusterIdThatIsNotTheHosts_IsRefused()
    {
        await using var server = await TestServer.LeaseAsync();
        var kubernetes = new Uri(server.Target.Kubernetes.Server);

        var refused = TargetGuard.RefuseCluster(kubernetes, "another-cluster", await server.Cluster.ReadMarkerAsync());

        await Assert.That(refused).IsEqualTo(
            $"Refusing the cluster at {kubernetes}: it is test cluster '{server.Target.ClusterId}', not 'another-cluster' as the descriptor says.");
    }
}
