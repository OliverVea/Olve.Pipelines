using Olve.Pipelines.Configuration;

namespace Olve.Pipelines.Kubernetes;

public class OpenBaoCredentialsProvider(
    OpenBaoClient openBaoClient,
    string secretPath = "external/data/olve-pipelines/k8s") : ICredentialsProvider<KubernetesCredentials>
{
    private KubernetesCredentials? _cached;

    public async Task<KubernetesCredentials> GetCredentialsAsync(CancellationToken ct = default)
    {
        if (_cached is not null)
            return _cached;

        var data = await openBaoClient.ReadSecretAsync(secretPath, ct);

        if (!data.TryGetValue("token", out var token) ||
            !data.TryGetValue("server", out var server) ||
            !data.TryGetValue("ca", out var ca))
        {
            throw new InvalidOperationException($"OpenBao secret at '{secretPath}' missing required fields (token, server, ca).");
        }

        var ns = data.GetValueOrDefault("namespace") ?? "olve-runners";

        _cached = new KubernetesCredentials(token, server, ca, ns);
        return _cached;
    }
}
