using System.Text.Json.Serialization;

namespace Olve.Pipelines.Kubernetes;

// K8s Job manifest for POST /apis/batch/v1/namespaces/{ns}/jobs
public record K8sJobManifest(
    [property: JsonPropertyName("apiVersion")] string ApiVersion,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("metadata")] K8sMetadata Metadata,
    [property: JsonPropertyName("spec")] K8sJobSpecBody Spec);

public record K8sMetadata(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("labels")] Dictionary<string, string>? Labels = null,
    [property: JsonPropertyName("creationTimestamp"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? CreationTimestamp = null);

public record K8sJobSpecBody(
    [property: JsonPropertyName("template")] K8sPodTemplateSpec Template,
    [property: JsonPropertyName("backoffLimit")] int BackoffLimit = 0,
    [property: JsonPropertyName("ttlSecondsAfterFinished")] int? TtlSecondsAfterFinished = null,
    [property: JsonPropertyName("activeDeadlineSeconds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ActiveDeadlineSeconds = null);

public record K8sPodTemplateSpec(
    [property: JsonPropertyName("metadata")] K8sMetadata? Metadata,
    [property: JsonPropertyName("spec")] K8sPodSpec Spec);

public record K8sPodSpec(
    [property: JsonPropertyName("containers")] K8sContainer[] Containers,
    [property: JsonPropertyName("initContainers")] K8sContainer[]? InitContainers = null,
    [property: JsonPropertyName("volumes")] K8sVolume[]? Volumes = null,
    [property: JsonPropertyName("restartPolicy")] string RestartPolicy = "Never",
    [property: JsonPropertyName("runtimeClassName")] string? RuntimeClassName = null,
    [property: JsonPropertyName("securityContext")] K8sPodSecurityContext? SecurityContext = null,
    [property: JsonPropertyName("automountServiceAccountToken"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AutomountServiceAccountToken = null,
    [property: JsonPropertyName("enableServiceLinks"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? EnableServiceLinks = null,
    [property: JsonPropertyName("serviceAccountName"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ServiceAccountName = null);

public record K8sPodSecurityContext(
    [property: JsonPropertyName("seccompProfile")] K8sSeccompProfile? SeccompProfile = null);

public record K8sSeccompProfile(
    [property: JsonPropertyName("type")] string Type);

public record K8sContainerSecurityContext(
    [property: JsonPropertyName("allowPrivilegeEscalation")] bool? AllowPrivilegeEscalation = null,
    [property: JsonPropertyName("runAsUser")] long? RunAsUser = null);

public record K8sContainer(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("image")] string Image,
    [property: JsonPropertyName("command")] string[]? Command = null,
    [property: JsonPropertyName("args")] string[]? Args = null,
    [property: JsonPropertyName("env")] K8sEnvVar[]? Env = null,
    [property: JsonPropertyName("envFrom")] K8sEnvFromSource[]? EnvFrom = null,
    [property: JsonPropertyName("volumeMounts")] K8sVolumeMount[]? VolumeMounts = null,
    [property: JsonPropertyName("securityContext")] K8sContainerSecurityContext? SecurityContext = null,
    [property: JsonPropertyName("imagePullPolicy"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ImagePullPolicy = null,
    [property: JsonPropertyName("resources"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] K8sResourceRequirements? Resources = null);

public record K8sResourceRequirements(
    [property: JsonPropertyName("requests"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<string, string>? Requests = null,
    [property: JsonPropertyName("limits"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<string, string>? Limits = null);

public record K8sEnvVar(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Value,
    [property: JsonPropertyName("valueFrom"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] K8sEnvVarSource? ValueFrom = null)
{
    /// <summary>An env var read from one key of a Secret (<c>valueFrom.secretKeyRef</c>).</summary>
    public static K8sEnvVar FromSecret(string name, string secretName, string key, bool optional = false)
        => new(name, null, new K8sEnvVarSource(new K8sSecretKeySelector(secretName, key, optional)));
}

public record K8sEnvVarSource(
    [property: JsonPropertyName("secretKeyRef")] K8sSecretKeySelector SecretKeyRef);

public record K8sSecretKeySelector(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("optional")] bool Optional = false);

public record K8sEnvFromSource(
    [property: JsonPropertyName("secretRef")] K8sSecretRef SecretRef);

public record K8sSecretRef(
    [property: JsonPropertyName("name")] string Name);

public record K8sVolume(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("emptyDir")] K8sEmptyDir? EmptyDir = null);

public record K8sEmptyDir(
    [property: JsonPropertyName("sizeLimit"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SizeLimit = null);

public record K8sVolumeMount(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("mountPath")] string MountPath,
    [property: JsonPropertyName("readOnly")] bool? ReadOnly = null);

// K8s Job status response
public record K8sJobResponse(
    [property: JsonPropertyName("metadata")] K8sMetadata Metadata,
    [property: JsonPropertyName("status")] K8sJobStatusBody? Status);

public record K8sJobStatusBody(
    [property: JsonPropertyName("conditions")] K8sJobCondition[]? Conditions,
    [property: JsonPropertyName("succeeded")] int? Succeeded,
    [property: JsonPropertyName("failed")] int? Failed,
    [property: JsonPropertyName("active")] int? Active);

public record K8sJobCondition(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("reason")] string? Reason = null);

// K8s Pod list for log retrieval
public record K8sPodList(
    [property: JsonPropertyName("items")] K8sPodItem[] Items);

public record K8sPodItem(
    [property: JsonPropertyName("metadata")] K8sMetadata Metadata,
    [property: JsonPropertyName("status")] K8sPodStatus? Status = null);

public record K8sPodStatus(
    [property: JsonPropertyName("phase")] string? Phase = null,
    [property: JsonPropertyName("reason")] string? Reason = null,
    [property: JsonPropertyName("initContainerStatuses")] K8sContainerStatus[]? InitContainerStatuses = null,
    [property: JsonPropertyName("containerStatuses")] K8sContainerStatus[]? ContainerStatuses = null);

public record K8sContainerStatus(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("state")] K8sContainerState? State = null);

public record K8sContainerState(
    [property: JsonPropertyName("waiting")] K8sContainerStateWaiting? Waiting = null,
    [property: JsonPropertyName("terminated")] K8sContainerStateTerminated? Terminated = null);

public record K8sContainerStateWaiting(
    [property: JsonPropertyName("reason")] string? Reason = null,
    [property: JsonPropertyName("message")] string? Message = null);

public record K8sContainerStateTerminated(
    [property: JsonPropertyName("exitCode")] int ExitCode,
    [property: JsonPropertyName("reason")] string? Reason = null,
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("startedAt")] DateTimeOffset? StartedAt = null,
    [property: JsonPropertyName("finishedAt")] DateTimeOffset? FinishedAt = null);

// K8s Secret
public record K8sSecret(
    [property: JsonPropertyName("apiVersion")] string ApiVersion,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("metadata")] K8sMetadata Metadata,
    [property: JsonPropertyName("type")] string Type = "Opaque",
    [property: JsonPropertyName("data")] Dictionary<string, string>? Data = null);

/// <summary>One line of a watch stream (<c>ADDED</c>, <c>MODIFIED</c>, <c>DELETED</c>, <c>ERROR</c>); only the object's metadata is read.</summary>
public record K8sWatchEvent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("object")] K8sWatchObject? Object = null);

public record K8sWatchObject([property: JsonPropertyName("metadata")] K8sMetadata? Metadata = null);

[JsonSerializable(typeof(K8sJobManifest))]
[JsonSerializable(typeof(K8sJobResponse))]
[JsonSerializable(typeof(K8sPodList))]
[JsonSerializable(typeof(K8sWatchEvent))]
[JsonSerializable(typeof(K8sSecret))]
internal partial class KubernetesJsonContext : JsonSerializerContext;
