using System.Text.Json;
using System.Text.Json.Serialization;

namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>
/// The compiled in-repo configuration manifest — the deserialization target for
/// <c>.pipelines/config.yaml</c>. It is a richer superset of <see cref="PipelineDocument"/>:
/// the document carries the reconciled pipeline shape, while <see cref="Description"/>,
/// <see cref="Version"/> and the <see cref="Secrets"/> declaration list live here so
/// <see cref="PipelineDocument"/> stays unpolluted.
/// </summary>
public record PipelineManifest(
    string ApiVersion,
    string Name,
    string? Description,
    string? Version,
    IReadOnlyList<SecretDeclaration>? Secrets,
    IReadOnlyList<ProductionStepDocument>? ProductionSteps,
    IReadOnlyList<ProcessingStepDocument>? ProcessingSteps,
    IReadOnlyList<TriggerDocument>? Triggers)
{
    /// <summary>The reconciled pipeline shape, with empty lists normalized.</summary>
    public PipelineDocument ToDocument() => new(
        ApiVersion,
        Name,
        ProductionSteps ?? [],
        ProcessingSteps ?? [],
        Triggers ?? []);
}

/// <summary>
/// A secret declared by NAME ONLY. The value never lives in the repo — it stays in the
/// pipeline's own k8s secret <c>olve-pipeline-{id:N}</c> and is referenced via
/// <c>$SECRET:NAME</c>.
/// </summary>
public record SecretDeclaration(string Name, string? Description);

/// <summary>
/// AOT-safe source-generated deserialization for <see cref="PipelineManifest"/>. The YAML
/// is first parsed into a <c>System.Text.Json</c> DOM (see <c>YamlJsonBridge</c>) and then
/// deserialized here, so all the existing <c>[JsonPolymorphic]</c> trigger-target metadata
/// is reused. YAML keys are camelCase; scalars arrive as strings, so number fields read via
/// <see cref="JsonNumberHandling.AllowReadingFromString"/>.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(PipelineManifest))]
internal partial class ManifestJsonContext : JsonSerializerContext;
