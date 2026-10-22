using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;

namespace Olve.Pipelines.Kubernetes.Harness;

/// <summary>Pre-signed URLs for one step attempt. They go into the per-attempt URL Secret, never the Job spec.</summary>
/// <param name="StepUrl">GET of <c>versions/&lt;p&gt;/&lt;n&gt;/step.tar.gz</c> (olve-init <c>OLVE_STEP_URL</c>).</param>
/// <param name="InputUrl">GET of the attempt's <c>input.json</c> (olve-init <c>OLVE_INPUT_URL</c>); null = no input file.</param>
/// <param name="ResultUrl">PUT for <c>result.tar.gz</c> (<c>OLVE_RESULT_URL</c>); null = the harness uploads nothing.</param>
/// <param name="FileUrls">The <c>OLVE_FILE_URLS</c> map (see <see cref="HarnessFileRefs.BuildUrlMapAsync"/>); empty = unset.</param>
public sealed record HarnessUrls(
    string StepUrl,
    string? InputUrl,
    string? ResultUrl,
    IReadOnlyDictionary<string, string> FileUrls);

/// <summary>Everything one step attempt's harness Job is built from.</summary>
/// <param name="JobName">K8s Job name (<see cref="Attempt.KubernetesJobName"/>).</param>
/// <param name="StepImage">The step's image from the pinned version's manifest.</param>
/// <param name="TimeoutSeconds">The manifest step's <c>timeoutSeconds</c> (null = default).</param>
/// <param name="Secrets">The step's declared secret names; each becomes one same-named env var.</param>
/// <param name="PipelineSecretName">The pipeline's K8s Secret (<c>olve-pipeline-&lt;idN&gt;</c>, see <see cref="HarnessJobBuilder.PipelineSecretName"/>).</param>
/// <param name="VersionStamp">The run's derived version (<c>OLVE_VERSION_STAMP</c>).</param>
/// <param name="Role">The <c>olve-role</c> label: <c>step</c> for attempts, <see cref="HarnessJobBuilder.FailureHandlerRole"/> for failure handlers.</param>
/// <param name="AttemptId">The attempt (names its URL Secret); null for failure handlers, which pass <paramref name="UrlSecretNameOverride"/>.</param>
/// <param name="UrlSecretNameOverride">The URL Secret's name; null = the attempt's (<see cref="HarnessStorageKeys.UrlSecretName"/>).</param>
public sealed record HarnessJobRequest(
    string JobName,
    Id<Attempt>? AttemptId,
    Id<Pipeline> PipelineId,
    Id<Run> RunId,
    int RunNumber,
    string StepName,
    int AttemptNumber,
    string VersionStamp,
    string StepImage,
    long? TimeoutSeconds,
    IReadOnlyList<string> Secrets,
    string PipelineSecretName,
    HarnessUrls Urls,
    string Role = HarnessJobBuilder.StepRole,
    string? UrlSecretNameOverride = null)
{
    /// <summary>Builds the request from an attempt; the step fields come from the pinned manifest.</summary>
    public static HarnessJobRequest ForAttempt(
        Attempt attempt, string versionStamp, string stepImage, long? timeoutSeconds,
        IReadOnlyList<string> secrets, HarnessUrls urls)
        => new(attempt.KubernetesJobName, attempt.Id, attempt.PipelineId, attempt.RunId, attempt.RunNumber,
            attempt.StepName, attempt.Number, versionStamp, stepImage, timeoutSeconds, secrets,
            PipelineSecrets.Name(attempt.PipelineId), urls);
}

/// <summary>A <c>map-trigger</c> Job: the version's binary maps one event to pipeline input.</summary>
/// <param name="StepUrl">Pre-signed GET of the version's <c>step.tar.gz</c>.</param>
/// <param name="EventUrl">Pre-signed GET of the event JSON (becomes <c>/olve/input.json</c>).</param>
public sealed record MapTriggerJobRequest(string JobName, Id<Pipeline> PipelineId, string TriggerName, string StepUrl, string EventUrl);

/// <summary>
/// A built Job plus the Secret it depends on. Create <see cref="UrlSecretName"/> with
/// <see cref="UrlSecretData"/> first, then the Job; delete the Secret once the Job is terminal (not on
/// cancellation — a restarted controller reattaches and the pod may still need it).
/// </summary>
public sealed record HarnessJobSpec(K8sJobManifest Job, string UrlSecretName, Dictionary<string, string> UrlSecretData);

/// <summary>
/// Builds the v2 step-pod Job exactly per harness/README.md "Pod template": harness-base init container
/// (<c>olve-init</c>, emptyDir at <c>/shared</c>) fetching the binary + input, then the step image running
/// the binary through the bundled loader. Pure.
/// </summary>
public static partial class HarnessJobBuilder
{
    public const string InitContainerName = "olve-init";
    public const string StepContainerName = "step";
    public const string ScratchVolumeName = "olve";

    /// <summary><c>olve-role</c> of step attempt Jobs.</summary>
    public const string StepRole = "step";

    /// <summary><c>olve-role</c> of v2 failure-handler Jobs (v1's bare handler Jobs use <c>failure-handler</c>).</summary>
    public const string FailureHandlerRole = "on-failure";

    public const string StepUrlKey = "OLVE_STEP_URL";
    public const string InputUrlKey = "OLVE_INPUT_URL";
    public const string ResultUrlKey = "OLVE_RESULT_URL";
    public const string FileUrlsKey = "OLVE_FILE_URLS";

    /// <summary>Env names the service sets itself; a declared secret must not shadow them.</summary>
    private static readonly HashSet<string> ReservedEnv = new(StringComparer.Ordinal)
    {
        "OLVE_DIR", "TMPDIR", "OLVE_PIPELINE", "OLVE_RUN_ID", "OLVE_RUN", "OLVE_ATTEMPT", "OLVE_VERSION_STAMP",
        StepUrlKey, InputUrlKey, ResultUrlKey, FileUrlsKey,
    };

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex DnsLabelRegex();

    [GeneratedRegex("^[A-Z_][A-Z0-9_]*$")]
    private static partial Regex SecretNameRegex();

    /// <summary>The pipeline's K8s Secret name (same convention as v1 and the secret store).</summary>

    public static Result<HarnessJobSpec> Build(HarnessJobRequest request, HarnessOptions options)
    {
        if (!DnsLabelRegex().IsMatch(request.JobName))
            return new ResultProblem("Invalid K8s Job name '{0}'.", request.JobName);
        if (!DnsLabelRegex().IsMatch(request.StepName))
            return new ResultProblem("Invalid step name '{0}'.", request.StepName);
        if (string.IsNullOrWhiteSpace(request.StepImage))
            return new ResultProblem("Step '{0}' has no image.", request.StepName);
        foreach (var secret in request.Secrets)
        {
            if (!SecretNameRegex().IsMatch(secret) || ReservedEnv.Contains(secret))
                return new ResultProblem("Step '{0}' declares an invalid secret name '{1}'.", request.StepName, secret);
        }

        var urlSecretName = request.UrlSecretNameOverride
                            ?? (request.AttemptId is { } attemptId ? HarnessStorageKeys.UrlSecretName(attemptId) : null);
        if (urlSecretName is null)
            return new ResultProblem("Job '{0}' has neither an attempt nor a URL Secret name.", request.JobName);
        var secretData = new Dictionary<string, string>(StringComparer.Ordinal) { [StepUrlKey] = request.Urls.StepUrl };
        if (request.Urls.InputUrl is not null) secretData[InputUrlKey] = request.Urls.InputUrl;
        if (request.Urls.ResultUrl is not null) secretData[ResultUrlKey] = request.Urls.ResultUrl;
        if (request.Urls.FileUrls.Count > 0)
        {
            var json = HarnessFileRefs.ToJson(request.Urls.FileUrls);
            // TODO(B5+): fall back to the SDK's files.json (fetched by olve-init) when the map outgrows an env var.
            if (Encoding.UTF8.GetByteCount(json) > HarnessFileRefs.MaxFileUrlsJsonBytes)
                return new ResultProblem("Step '{0}' has {1} input file refs; the URL map exceeds {2} bytes.",
                    request.StepName, request.Urls.FileUrls.Count, HarnessFileRefs.MaxFileUrlsJsonBytes);
            secretData[FileUrlsKey] = json;
        }

        K8sEnvVar Url(string key) => K8sEnvVar.FromSecret(key, urlSecretName, key);

        var initEnv = new List<K8sEnvVar> { Url(StepUrlKey) };
        if (secretData.ContainsKey(InputUrlKey)) initEnv.Add(Url(InputUrlKey));

        var stepEnv = new List<K8sEnvVar>
        {
            new("OLVE_DIR", "/olve"),
            new("TMPDIR", "/olve/tmp"),
            new("OLVE_PIPELINE", request.PipelineId.Value.Value.ToString("N")),
            new("OLVE_RUN_ID", request.RunId.Value.Value.ToString("N")),
            new("OLVE_RUN", request.RunNumber.ToString(CultureInfo.InvariantCulture)),
            new("OLVE_ATTEMPT", request.AttemptNumber.ToString(CultureInfo.InvariantCulture)),
            new("OLVE_VERSION_STAMP", request.VersionStamp),
        };
        if (secretData.ContainsKey(ResultUrlKey)) stepEnv.Add(Url(ResultUrlKey));
        if (secretData.ContainsKey(FileUrlsKey)) stepEnv.Add(Url(FileUrlsKey));
        // Declared secrets only, one secretKeyRef each (key = name) — never envFrom of the whole Secret.
        foreach (var secret in request.Secrets.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            stepEnv.Add(K8sEnvVar.FromSecret(secret, request.PipelineSecretName, secret));

        var labels = new Dictionary<string, string>
        {
            ["app"] = "olve-pipelines",
            ["job-name"] = request.JobName,
            ["olve-role"] = request.Role,
            ["olve-pipeline"] = request.PipelineId.Value.Value.ToString("N"),
            ["olve-run"] = request.RunNumber.ToString(CultureInfo.InvariantCulture),
            ["olve-step"] = request.StepName,
            ["olve-attempt"] = request.AttemptNumber.ToString(CultureInfo.InvariantCulture),
        };

        var init = new K8sContainer(
            InitContainerName,
            options.HarnessBaseImage,
            Env: initEnv.ToArray(),
            VolumeMounts: [new K8sVolumeMount(ScratchVolumeName, "/shared")],
            SecurityContext: HarnessSecurity.Container,
            ImagePullPolicy: "IfNotPresent",
            Resources: new K8sResourceRequirements(
                Requests: new() { ["cpu"] = "100m", ["memory"] = "64Mi" },
                Limits: new() { ["memory"] = "256Mi" }));

        var step = new K8sContainer(
            StepContainerName,
            request.StepImage,
            Command: ["/olve/lib/ld-linux-x86-64.so.2", "--library-path", "/olve/lib", "/olve/step", "run", request.StepName],
            Env: stepEnv.ToArray(),
            VolumeMounts: [new K8sVolumeMount(ScratchVolumeName, "/olve")],
            SecurityContext: HarnessSecurity.Container,
            Resources: StepResources(options));

        var job = new K8sJobManifest(
            "batch/v1",
            "Job",
            new K8sMetadata(request.JobName, labels),
            new K8sJobSpecBody(
                new K8sPodTemplateSpec(
                    new K8sMetadata(request.JobName, labels),
                    new K8sPodSpec(
                        Containers: [step],
                        InitContainers: [init],
                        Volumes: [new K8sVolume(ScratchVolumeName, new K8sEmptyDir(options.ScratchSizeLimit))],
                        RestartPolicy: "Never",
                        RuntimeClassName: options.RuntimeClassName,
                        SecurityContext: HarnessSecurity.Pod,
                        AutomountServiceAccountToken: false,
                        EnableServiceLinks: false)),
                BackoffLimit: 0,
                TtlSecondsAfterFinished: options.TtlSecondsAfterFinished,
                ActiveDeadlineSeconds: options.DeadlineSeconds(request.TimeoutSeconds)));

        return new HarnessJobSpec(job, urlSecretName, secretData);
    }

    /// <summary>
    /// A short <c>map-trigger &lt;trigger&gt;</c> Job: olve-init fetches the version's binary and the event
    /// (as <c>input.json</c>); the main container is harness-base itself (already pulled; the emptyDir carries
    /// the loader). The mapped input is read from the result block in the pod log — no result upload, no
    /// secrets. Pure.
    /// </summary>
    public static Result<HarnessJobSpec> BuildMapTrigger(MapTriggerJobRequest request, HarnessOptions options)
    {
        if (!DnsLabelRegex().IsMatch(request.JobName))
            return new ResultProblem("Invalid K8s Job name '{0}'.", request.JobName);
        if (string.IsNullOrWhiteSpace(request.TriggerName) || request.TriggerName.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            return new ResultProblem("Invalid trigger name '{0}'.", request.TriggerName);

        var urlSecretName = $"{request.JobName}-urls";
        var secretData = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StepUrlKey] = request.StepUrl,
            [InputUrlKey] = request.EventUrl,
        };
        K8sEnvVar Url(string key) => K8sEnvVar.FromSecret(key, urlSecretName, key);

        var labels = new Dictionary<string, string>
        {
            ["app"] = "olve-pipelines",
            ["job-name"] = request.JobName,
            ["olve-role"] = "map-trigger",
            ["olve-pipeline"] = request.PipelineId.Value.Value.ToString("N"),
        };

        var init = new K8sContainer(
            InitContainerName,
            options.HarnessBaseImage,
            Env: [Url(StepUrlKey), Url(InputUrlKey)],
            VolumeMounts: [new K8sVolumeMount(ScratchVolumeName, "/shared")],
            SecurityContext: HarnessSecurity.Container,
            ImagePullPolicy: "IfNotPresent",
            Resources: new K8sResourceRequirements(
                Requests: new() { ["cpu"] = "100m", ["memory"] = "64Mi" },
                Limits: new() { ["memory"] = "256Mi" }));

        var main = new K8sContainer(
            StepContainerName,
            options.HarnessBaseImage,
            Command: ["/olve/lib/ld-linux-x86-64.so.2", "--library-path", "/olve/lib", "/olve/step", "map-trigger", request.TriggerName],
            Env:
            [
                new("OLVE_DIR", "/olve"),
                new("TMPDIR", "/olve/tmp"),
                new("OLVE_PIPELINE", request.PipelineId.Value.Value.ToString("N")),
            ],
            VolumeMounts: [new K8sVolumeMount(ScratchVolumeName, "/olve")],
            SecurityContext: HarnessSecurity.Container,
            ImagePullPolicy: "IfNotPresent",
            Resources: new K8sResourceRequirements(
                Requests: new() { ["cpu"] = "100m", ["memory"] = "128Mi" },
                Limits: new() { ["memory"] = "512Mi" }));

        var job = new K8sJobManifest(
            "batch/v1",
            "Job",
            new K8sMetadata(request.JobName, labels),
            new K8sJobSpecBody(
                new K8sPodTemplateSpec(
                    new K8sMetadata(request.JobName, labels),
                    new K8sPodSpec(
                        Containers: [main],
                        InitContainers: [init],
                        Volumes: [new K8sVolume(ScratchVolumeName, new K8sEmptyDir(options.ScratchSizeLimit))],
                        RestartPolicy: "Never",
                        RuntimeClassName: options.RuntimeClassName,
                        SecurityContext: HarnessSecurity.Pod,
                        AutomountServiceAccountToken: false,
                        EnableServiceLinks: false)),
                BackoffLimit: 0,
                TtlSecondsAfterFinished: options.MapTriggerTtlSeconds,
                ActiveDeadlineSeconds: options.MapTriggerTimeoutSeconds));

        return new HarnessJobSpec(job, urlSecretName, secretData);
    }

    private static K8sResourceRequirements? StepResources(HarnessOptions options)
    {
        var requests = new Dictionary<string, string>();
        var limits = new Dictionary<string, string>();
        if (options.StepCpuRequest is { } cpu) requests["cpu"] = cpu;
        if (options.StepMemoryRequest is { } mem) requests["memory"] = mem;
        if (options.StepMemoryLimit is { } limit) limits["memory"] = limit;
        return requests.Count == 0 && limits.Count == 0
            ? null
            : new K8sResourceRequirements(requests.Count > 0 ? requests : null, limits.Count > 0 ? limits : null);
    }
}

/// <summary>
/// Pod/container hardening shared with v1 step pods: no privilege escalation, RuntimeDefault seccomp.
/// Steps may run as root (kaniko etc.); isolation is gVisor + PSS baseline + NetworkPolicy.
/// </summary>
internal static class HarnessSecurity
{
    public static readonly K8sContainerSecurityContext Container = new(AllowPrivilegeEscalation: false);
    public static readonly K8sPodSecurityContext Pod = new(new K8sSeccompProfile("RuntimeDefault"));
}
