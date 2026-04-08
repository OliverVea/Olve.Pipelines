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
    [property: JsonPropertyName("labels")] Dictionary<string, string>? Labels = null);

public record K8sJobSpecBody(
    [property: JsonPropertyName("template")] K8sPodTemplateSpec Template,
    [property: JsonPropertyName("backoffLimit")] int BackoffLimit = 0);

public record K8sPodTemplateSpec(
    [property: JsonPropertyName("metadata")] K8sMetadata? Metadata,
    [property: JsonPropertyName("spec")] K8sPodSpec Spec);

public record K8sPodSpec(
    [property: JsonPropertyName("containers")] K8sContainer[] Containers,
    [property: JsonPropertyName("initContainers")] K8sContainer[]? InitContainers = null,
    [property: JsonPropertyName("volumes")] K8sVolume[]? Volumes = null,
    [property: JsonPropertyName("restartPolicy")] string RestartPolicy = "Never");

public record K8sContainer(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("image")] string Image,
    [property: JsonPropertyName("command")] string[]? Command = null,
    [property: JsonPropertyName("args")] string[]? Args = null,
    [property: JsonPropertyName("env")] K8sEnvVar[]? Env = null,
    [property: JsonPropertyName("envFrom")] K8sEnvFromSource[]? EnvFrom = null,
    [property: JsonPropertyName("volumeMounts")] K8sVolumeMount[]? VolumeMounts = null);

public record K8sEnvVar(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] string Value);

public record K8sEnvFromSource(
    [property: JsonPropertyName("secretRef")] K8sSecretRef SecretRef);

public record K8sSecretRef(
    [property: JsonPropertyName("name")] string Name);

public record K8sVolume(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("emptyDir")] K8sEmptyDir? EmptyDir = null);

public record K8sEmptyDir;

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
    [property: JsonPropertyName("message")] string? Message);

// K8s Pod list for log retrieval
public record K8sPodList(
    [property: JsonPropertyName("items")] K8sPodItem[] Items);

public record K8sPodItem(
    [property: JsonPropertyName("metadata")] K8sMetadata Metadata);

// K8s Secret
public record K8sSecret(
    [property: JsonPropertyName("apiVersion")] string ApiVersion,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("metadata")] K8sMetadata Metadata,
    [property: JsonPropertyName("type")] string Type = "Opaque",
    [property: JsonPropertyName("data")] Dictionary<string, string>? Data = null);

[JsonSerializable(typeof(K8sJobManifest))]
[JsonSerializable(typeof(K8sJobResponse))]
[JsonSerializable(typeof(K8sPodList))]
[JsonSerializable(typeof(K8sSecret))]
internal partial class KubernetesJsonContext : JsonSerializerContext;
