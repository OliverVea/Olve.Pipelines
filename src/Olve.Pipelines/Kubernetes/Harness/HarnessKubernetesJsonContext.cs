using System.Text.Json.Serialization;

namespace Olve.Pipelines.Kubernetes.Harness;

/// <summary>
/// Serializer for harness/build Job manifests and secrets: omits nulls (unlike
/// <see cref="KubernetesJsonContext"/>, whose output for v1-style manifests stays unchanged), so the
/// manifest is exactly the fields the builders set.
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true)]
[JsonSerializable(typeof(K8sJobManifest))]
[JsonSerializable(typeof(K8sSecret))]
internal partial class HarnessKubernetesJsonContext : JsonSerializerContext;
