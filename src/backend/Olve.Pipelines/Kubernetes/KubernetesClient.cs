using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Olve.Pipelines.Kubernetes;

public partial class KubernetesClient : IKubernetesClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<KubernetesClient>? _logger;

    /// <param name="tokenProvider">
    /// When supplied, the bearer token is re-read from here on every request (used for
    /// rotating in-cluster ServiceAccount tokens). When null, the static
    /// <see cref="KubernetesCredentials.Token"/> is used for the client's lifetime.
    /// </param>
    public KubernetesClient(
        KubernetesCredentials credentials,
        ILogger<KubernetesClient>? logger = null,
        Func<CancellationToken, ValueTask<string>>? tokenProvider = null)
    {
        _logger = logger;

        var baseHandler = new HttpClientHandler();

        if (!string.IsNullOrEmpty(credentials.CaCertificate))
        {
            var caCert = credentials.CaCertificate.Contains("BEGIN CERTIFICATE")
                ? X509Certificate2.CreateFromPem(credentials.CaCertificate)
                : X509CertificateLoader.LoadCertificate(Convert.FromBase64String(credentials.CaCertificate));
            baseHandler.ServerCertificateCustomValidationCallback = (_, cert, chain, errors) =>
            {
                if (errors == System.Net.Security.SslPolicyErrors.None) return true;

                using var customChain = new X509Chain();
                customChain.ChainPolicy.ExtraStore.Add(caCert);
                customChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                customChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                customChain.ChainPolicy.CustomTrustStore.Add(caCert);
                return cert is not null && customChain.Build(cert);
            };
        }

        HttpMessageHandler handler = tokenProvider is not null
            ? new BearerTokenHandler(tokenProvider) { InnerHandler = baseHandler }
            : baseHandler;

        _httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(credentials.Server.TrimEnd('/') + "/"),
        };

        if (tokenProvider is null)
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", credentials.Token);
        }
    }

    /// <summary>Sets a fresh bearer token from the provider on each outgoing request.</summary>
    private sealed class BearerTokenHandler(Func<CancellationToken, ValueTask<string>> tokenProvider)
        : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", await tokenProvider(cancellationToken));
            return await base.SendAsync(request, cancellationToken);
        }
    }

    public async Task<KubernetesJobStatus> GetJobStatusAsync(string ns, string jobName, CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync(
            $"apis/batch/v1/namespaces/{ns}/jobs/{jobName}", ct);

        response.EnsureSuccessStatusCode();

        var jobResponse = await response.Content.ReadFromJsonAsync(
            KubernetesJsonContext.Default.K8sJobResponse, ct);

        return ToJobStatus(jobName, jobResponse);
    }

    public async Task<KubernetesJobStatus?> TryGetJobStatusAsync(string ns, string jobName, CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync(
            $"apis/batch/v1/namespaces/{ns}/jobs/{jobName}", ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();

        var jobResponse = await response.Content.ReadFromJsonAsync(
            KubernetesJsonContext.Default.K8sJobResponse, ct);

        return ToJobStatus(jobName, jobResponse);
    }

    private static KubernetesJobStatus ToJobStatus(string jobName, K8sJobResponse? jobResponse)
    {
        var status = jobResponse?.Status;
        var phase = status switch
        {
            { Succeeded: > 0 } => JobPhase.Succeeded,
            { Failed: > 0 } => JobPhase.Failed,
            { Active: > 0 } => JobPhase.Running,
            _ => JobPhase.Pending,
        };

        var message = status?.Conditions?.FirstOrDefault()?.Message;

        return new KubernetesJobStatus(jobName, phase, message);
    }

    public async Task<string?> GetPodLogsAsync(string ns, string jobName, string? container = null, CancellationToken ct = default)
    {
        var podListResponse = await _httpClient.GetAsync(
            $"api/v1/namespaces/{ns}/pods?labelSelector=batch.kubernetes.io/job-name={jobName}", ct);

        podListResponse.EnsureSuccessStatusCode();

        var podList = await podListResponse.Content.ReadFromJsonAsync(
            KubernetesJsonContext.Default.K8sPodList, ct);

        var podName = podList?.Items.FirstOrDefault()?.Metadata.Name;
        if (podName is null) return null;

        var containerQuery = container is not null ? $"?container={container}" : "";
        var logResponse = await _httpClient.GetAsync(
            $"api/v1/namespaces/{ns}/pods/{podName}/log{containerQuery}", ct);

        logResponse.EnsureSuccessStatusCode();

        return await logResponse.Content.ReadAsStringAsync(ct);
    }

    public async Task DeleteJobAsync(string ns, string jobName, CancellationToken ct = default)
    {
        _logger?.LogInformation("Deleting K8s Job {JobName} in namespace {Namespace}", jobName, ns);

        // propagationPolicy=Background ensures the Job's Pods are also cleaned up
        var response = await _httpClient.DeleteAsync(
            $"apis/batch/v1/namespaces/{ns}/jobs/{jobName}?propagationPolicy=Background", ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger?.LogWarning("Failed to delete Job {JobName}: {StatusCode} {Body}", jobName, response.StatusCode, body);
        }
    }

    public async Task CreateSecretAsync(string ns, string secretName, Dictionary<string, string> data, CancellationToken ct = default)
    {
        var secret = new K8sSecret(
            "v1",
            "Secret",
            new K8sMetadata(secretName),
            Data: EncodeSecretData(data));

        var response = await _httpClient.PostAsJsonAsync(
            $"api/v1/namespaces/{ns}/secrets",
            secret,
            KubernetesJsonContext.Default.K8sSecret,
            ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            _logger?.LogInformation("K8s Secret '{SecretName}' already exists — reusing (likely reattach after restart)", secretName);
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Failed to create K8s Secret '{secretName}': {response.StatusCode} {body}", null, response.StatusCode);
        }
    }

    public async Task<Dictionary<string, string>?> GetSecretAsync(string ns, string secretName, CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync(
            $"api/v1/namespaces/{ns}/secrets/{secretName}", ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();

        var secret = await response.Content.ReadFromJsonAsync(
            KubernetesJsonContext.Default.K8sSecret, ct);

        return secret?.Data?.ToDictionary(
            kvp => kvp.Key,
            kvp => Encoding.UTF8.GetString(Convert.FromBase64String(kvp.Value)));
    }

    public async Task UpdateSecretAsync(string ns, string secretName, Dictionary<string, string> data, CancellationToken ct = default)
    {
        var secret = new K8sSecret(
            "v1",
            "Secret",
            new K8sMetadata(secretName),
            Data: EncodeSecretData(data));

        var request = new HttpRequestMessage(HttpMethod.Put,
            $"api/v1/namespaces/{ns}/secrets/{secretName}")
        {
            Content = JsonContent.Create(secret, KubernetesJsonContext.Default.K8sSecret),
        };

        var response = await _httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Failed to update K8s Secret '{secretName}': {response.StatusCode} {body}", null, response.StatusCode);
        }
    }

    public async Task DeleteSecretAsync(string ns, string secretName, CancellationToken ct = default)
    {
        var response = await _httpClient.DeleteAsync(
            $"api/v1/namespaces/{ns}/secrets/{secretName}", ct);

        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger?.LogWarning("Failed to delete Secret {SecretName}: {StatusCode} {Body}", secretName, response.StatusCode, body);
        }
    }

    // K8s deletes a finished step Job (and its pod) after this. The controller copies the pod's logs
    // to storage as soon as it sees the Job finish, so this only has to outlast a controller outage —
    // a week is generous.
    internal const int StepJobTtlSeconds = 7 * 24 * 60 * 60;

    private static Dictionary<string, string> EncodeSecretData(Dictionary<string, string> data)
        => data.ToDictionary(
            kvp => kvp.Key,
            kvp => Convert.ToBase64String(Encoding.UTF8.GetBytes(kvp.Value)));

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
