using Olve.Pipelines.Configuration;

namespace Olve.Pipelines.Kubernetes;

/// <summary>
/// Cluster credentials from the pod's own ServiceAccount — the idiomatic in-cluster auth,
/// replacing the OpenBao→Authentik token chain (Tier-A / <c>Kubernetes:AuthMode=InCluster</c>).
/// The server is read from the injected <c>KUBERNETES_SERVICE_*</c> env; the token, CA, and
/// namespace from the projected ServiceAccount mount. Projected tokens rotate (~hourly), so
/// the token is re-read per request via <see cref="ReadTokenAsync"/> rather than cached.
/// </summary>
public sealed class InClusterCredentialsProvider : ICredentialsProvider<KubernetesCredentials>
{
    private const string TokenPath = "/var/run/secrets/kubernetes.io/serviceaccount/token";
    private const string CaPath = "/var/run/secrets/kubernetes.io/serviceaccount/ca.crt";
    private const string NamespacePath = "/var/run/secrets/kubernetes.io/serviceaccount/namespace";

    public async Task<KubernetesCredentials> GetCredentialsAsync(CancellationToken ct = default)
    {
        var host = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST");
        if (string.IsNullOrEmpty(host))
            throw new InvalidOperationException("Not running in-cluster: KUBERNETES_SERVICE_HOST is not set.");

        if (!File.Exists(TokenPath))
            throw new InvalidOperationException($"ServiceAccount token not found at '{TokenPath}' — is automountServiceAccountToken disabled?");

        var port = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT") ?? "443";
        var token = (await File.ReadAllTextAsync(TokenPath, ct)).Trim();
        var ca = await File.ReadAllTextAsync(CaPath, ct);
        var ns = (await File.ReadAllTextAsync(NamespacePath, ct)).Trim();

        return new KubernetesCredentials(token, $"https://{host}:{port}", ca, ns);
    }

    /// <summary>Reads the current ServiceAccount token (re-read per request to survive rotation).</summary>
    public static async ValueTask<string> ReadTokenAsync(CancellationToken ct = default)
        => (await File.ReadAllTextAsync(TokenPath, ct)).Trim();
}
