using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Kubernetes.Harness;

namespace Olve.Pipelines.RealSystemTests.Kubernetes;

/// <summary>Small Jobs shaped like the harness's: an olve-init init container and a step container. A short TTL collects them.</summary>
internal static class TestJobs
{
    private const string Image = "alpine:3.22";

    public static K8sJobManifest WithInit(string name, string script, string init = "true", string image = Image, long deadlineSeconds = 60, K8sEnvVar[]? env = null)
    {
        var labels = new Dictionary<string, string> { ["app"] = "olve-pipelines" };
        return new K8sJobManifest(
            "batch/v1", "Job",
            new K8sMetadata(name, labels),
            new K8sJobSpecBody(
                new K8sPodTemplateSpec(
                    new K8sMetadata(name, labels),
                    new K8sPodSpec(
                        Containers: [new K8sContainer("step", image, ["/bin/sh", "-c", script], Env: env)],
                        InitContainers: [new K8sContainer(HarnessJobBuilder.InitContainerName, Image, ["/bin/sh", "-c", init])])),
                BackoffLimit: 0,
                TtlSecondsAfterFinished: 120,
                ActiveDeadlineSeconds: deadlineSeconds));
    }

    public static async Task<HarnessPodState> SubmitAndWaitAsync(TestCluster cluster, K8sJobManifest job, Func<HarnessPodState, bool> done)
    {
        await cluster.Client.CreateJobAsync(cluster.Namespace, job);
        return (await Wait.UntilAsync(
            () => cluster.Client.GetHarnessPodStateAsync(cluster.Namespace, job.Metadata.Name),
            state => state is not null && done(state), Wait.Cluster, $"Job {job.Metadata.Name}"))!;
    }
}
