using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.RealSystemTests.Contracts;

namespace Olve.Pipelines.RealSystemTests.Kubernetes;

[InheritsTests]
public class RealKubernetesSecrets : KubernetesSecretsContract
{
    protected override async Task<ContractSubject<IKubernetesClient>> SubjectAsync()
    {
        var server = await TestServer.LeaseAsync();
        return new(server.Cluster.Client, server.Cluster.Namespace, () => TestServer.NewName("secret"), server);
    }
}

[InheritsTests]
public class RealHarnessCluster : HarnessClusterContract
{
    protected override async Task<ContractSubject<Cluster>> SubjectAsync()
    {
        var server = await TestServer.LeaseAsync();
        return new(new Cluster(server.Cluster.Client, server.Cluster.Client), server.Cluster.Namespace, () => TestServer.NewName("job"), server);
    }

    protected override async Task<HarnessPodState> RunningStateAsync()
    {
        await using var server = await TestServer.LeaseAsync();
        var job = TestJobs.WithInit(TestServer.NewName("job"), "sleep 30");
        return await TestJobs.SubmitAndWaitAsync(server.Cluster, job, s => s is { Init.ExitCode: 0, Main: { Terminated: false, Reason: null } });
    }

    protected override async Task<HarnessPodState> ExitedCleanlyStateAsync()
    {
        await using var server = await TestServer.LeaseAsync();
        var job = TestJobs.WithInit(TestServer.NewName("job"), "true");
        return await TestJobs.SubmitAndWaitAsync(server.Cluster, job, HarnessExitCodes.IsFinished);
    }
}
