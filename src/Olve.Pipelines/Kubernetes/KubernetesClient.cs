using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Olve.Pipelines.Kubernetes;

public class KubernetesClient : IKubernetesClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<KubernetesClient>? _logger;

    public KubernetesClient(
        KubernetesCredentials credentials,
        ILogger<KubernetesClient>? logger = null)
    {
        _logger = logger;

        var handler = new HttpClientHandler();

        if (!string.IsNullOrEmpty(credentials.CaCertificate))
        {
            var caCert = credentials.CaCertificate.Contains("BEGIN CERTIFICATE")
                ? X509Certificate2.CreateFromPem(credentials.CaCertificate)
                : X509CertificateLoader.LoadCertificate(Convert.FromBase64String(credentials.CaCertificate));
            handler.ServerCertificateCustomValidationCallback = (_, cert, chain, errors) =>
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

        _httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(credentials.Server.TrimEnd('/') + "/"),
        };
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", credentials.Token);
    }

    public async Task CreateJobAsync(string ns, KubernetesJobSpec spec, CancellationToken ct = default)
    {
        var manifest = BuildJobManifest(spec);

        _logger?.LogInformation("Creating K8s Job {JobName} in namespace {Namespace}", spec.Name, ns);

        var response = await _httpClient.PostAsJsonAsync(
            $"apis/batch/v1/namespaces/{ns}/jobs",
            manifest,
            KubernetesJsonContext.Default.K8sJobManifest,
            ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger?.LogError("Failed to create Job {JobName}: {StatusCode} {Body}", spec.Name, response.StatusCode, body);
            throw new InvalidOperationException($"Failed to create K8s Job '{spec.Name}': {response.StatusCode}");
        }

        _logger?.LogInformation("K8s Job {JobName} created", spec.Name);
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
            throw new InvalidOperationException($"Failed to create K8s Secret '{secretName}': {response.StatusCode} {body}");
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
            throw new InvalidOperationException($"Failed to update K8s Secret '{secretName}': {response.StatusCode} {body}");
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

    private static K8sJobManifest BuildJobManifest(KubernetesJobSpec spec)
    {
        var labels = new Dictionary<string, string>
        {
            ["app"] = "olve-pipelines",
            ["job-name"] = spec.Name,
        };

        var volumes = new[]
        {
            new K8sVolume("input", new K8sEmptyDir()),
            new K8sVolume("output", new K8sEmptyDir()),
        };

        // Runner env vars (user-defined + pipeline secrets)
        var runnerEnv = new List<K8sEnvVar>();
        if (spec.EnvironmentVariables is not null)
        {
            foreach (var (key, value) in spec.EnvironmentVariables)
                runnerEnv.Add(new K8sEnvVar(key, value));
        }

        var runnerEnvFrom = spec.SecretName is not null
            ? new[] { new K8sEnvFromSource(new K8sSecretRef(spec.SecretName)) }
            : (K8sEnvFromSource[]?)null;

        var runnerVolumeMounts = new[]
        {
            new K8sVolumeMount("input", "/input", ReadOnly: true),
            new K8sVolumeMount("output", "/output"),
        };

        var runner = new K8sContainer(
            "runner",
            spec.Image,
            Command: ["/bin/sh", "-c"],
            Args: [spec.Script],
            Env: runnerEnv.Count > 0 ? runnerEnv.ToArray() : null,
            EnvFrom: runnerEnvFrom,
            VolumeMounts: runnerVolumeMounts);

        // S3 helper env/credentials
        var s3HelperEnvFrom = new[] { new K8sEnvFromSource(new K8sSecretRef(spec.S3CredentialsSecretName)) };

        // Build init containers
        var initContainers = new List<K8sContainer>();

        var insecureFlag = spec.S3SkipCertValidation ? " --insecure" : "";

        // s3-download init container (processing jobs with input)
        if (spec.InputBundleS3Prefix is not null)
        {
            var downloadScript = $"mc mirror{insecureFlag} s3/{spec.S3Bucket}/{spec.InputBundleS3Prefix}/ /input/";

            initContainers.Add(new K8sContainer(
                "s3-download",
                spec.S3HelperImage,
                Command: ["/bin/sh", "-c"],
                Args: [downloadScript],
                EnvFrom: s3HelperEnvFrom,
                VolumeMounts: [new K8sVolumeMount("input", "/input")]));
        }

        // Runner runs as init container (so upload only happens on success)
        initContainers.Add(runner);

        // Main container: s3-upload
        var uploadScript = $"mc mirror{insecureFlag} /output/ s3/{spec.S3Bucket}/{spec.OutputBundleS3Prefix}/";

        var s3Upload = new K8sContainer(
            "s3-upload",
            spec.S3HelperImage,
            Command: ["/bin/sh", "-c"],
            Args: [uploadScript],
            EnvFrom: s3HelperEnvFrom,
            VolumeMounts: [new K8sVolumeMount("output", "/output", ReadOnly: true)]);

        return new K8sJobManifest(
            "batch/v1",
            "Job",
            new K8sMetadata(spec.Name, labels),
            new K8sJobSpecBody(
                new K8sPodTemplateSpec(
                    new K8sMetadata(spec.Name, labels),
                    new K8sPodSpec(
                        Containers: [s3Upload],
                        InitContainers: initContainers.ToArray(),
                        Volumes: volumes))));
    }

    private static Dictionary<string, string> EncodeSecretData(Dictionary<string, string> data)
        => data.ToDictionary(
            kvp => kvp.Key,
            kvp => Convert.ToBase64String(Encoding.UTF8.GetBytes(kvp.Value)));

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
