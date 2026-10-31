using Olve.Pipelines.Kubernetes;

namespace Olve.Pipelines.RealSystemTests.Hosting;

/// <summary>The target's cluster, seen with the server's own ServiceAccount, so its RBAC is part of every test.</summary>
public sealed class TestCluster(KubernetesTarget target)
{
    public KubernetesClient Client { get; } =
        new(new KubernetesCredentials(target.Token, target.Server, target.CaCertificate, target.Namespace));

    public string Namespace => target.Namespace;

    public async Task<string?> ReadMarkerAsync()
    {
        var marker = await Client.GetSecretAsync(Namespace, TargetGuard.MarkerSecret);
        return marker?.GetValueOrDefault(TargetGuard.MarkerKey);
    }

    public async Task<IReadOnlyList<string>> JobsOfAsync(Guid pipelineId)
    {
        var label = pipelineId.ToString("N");
        var jobs = await Client.ListJobsAsync(Namespace);
        return jobs.Where(j => j.Labels.GetValueOrDefault("olve-pipeline") == label).Select(j => j.Name).ToList();
    }

    public static string PipelineSecretName(Guid pipelineId) => $"olve-pipeline-{pipelineId:N}";
}
