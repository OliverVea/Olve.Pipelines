using System.Text.Json;

namespace Olve.Pipelines.Pipelines.Manifests;

// C# mirror of the v2 manifest (sdk/manifest.schema.json, generated from sdk/src/describe/manifest-schema.ts).
// Built only by ManifestParser (hand-written over JsonDocument: the format discriminates by key, and
// canonical key order puts a poll trigger's "headers" before its "kind", so [JsonPolymorphic] can't
// read it). JSON Schema views, constants, config and `when` values are kept as cloned JsonElements.

/// <summary>A parsed v2 manifest (<c>apiVersion: "2.0"</c>).</summary>
public sealed record Manifest(
    string ApiVersion,
    string Name,
    string? Description,
    string Input,
    ManifestHarness Harness,
    IReadOnlyDictionary<string, ModelSchemas> Models,
    IReadOnlyList<ManifestSecret> Secrets,
    IReadOnlyList<ManifestTrigger> Triggers,
    IReadOnlyList<ManifestStage> Stages,
    IReadOnlyList<ManifestFailureHandler> FailureHandlers)
{
    public const string SupportedApiVersion = "2.0";
}

/// <summary>Harness identity (already folded into every handler hash).</summary>
public sealed record ManifestHarness(string Bun, string Sdk, string SdkHash);

/// <summary>JSON Schema views of a model. <see cref="Input"/> is present only when it differs from <see cref="Output"/>.</summary>
public sealed record ModelSchemas(JsonElement Output, JsonElement? Input);

public sealed record ManifestSecret(string Name, string Description);

public abstract record ManifestTrigger(string Name);

/// <summary>GitHub push trigger (name is always <c>push</c>).</summary>
public sealed record PushTrigger() : ManifestTrigger(Name: PushName)
{
    public const string PushName = "push";
}

/// <summary>Background poller. Header values may reference <c>$SECRET:NAME</c> (NAME ∈ <see cref="Secrets"/>).</summary>
public sealed record PollTrigger(
    string Name,
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyList<string> Secrets,
    string ValuePath,
    long IntervalSeconds) : ManifestTrigger(Name);

public abstract record ManifestStage;

/// <summary>A barrier stage: its steps run in parallel.</summary>
public sealed record StepsStage(IReadOnlyList<StepDescriptor> Steps) : ManifestStage;

/// <summary>A conditional block: pipeline-input field → accepted values (string/number/boolean). Empty = always.</summary>
public sealed record WhenStage(
    IReadOnlyDictionary<string, IReadOnlyList<JsonElement>> When,
    IReadOnlyList<ManifestStage> Stages) : ManifestStage;

public sealed record StepDescriptor(
    string Name,
    string Kind,
    string Image,
    IReadOnlyDictionary<string, InputType> InputTypes,
    IReadOnlyDictionary<string, Mapping> Input,
    string? Output,
    IReadOnlyList<string> Secrets,
    string HandlerHash,
    long? TimeoutSeconds,
    long? Retries,
    IReadOnlyDictionary<string, JsonElement>? Config);

public abstract record InputType;

/// <summary>A model input; <see cref="Optional"/> = <c>optional(Model)</c> (may be absent because its producer was skipped).</summary>
public sealed record ModelInputType(string Model, bool Optional) : InputType;

/// <summary>A plain (unnamed) schema input. Always explicitly mapped.</summary>
public sealed record SchemaInputType(ModelSchemas Schema) : InputType;

/// <summary>Tagged mapping: <c>{ref}</c>, <c>{const}</c> or a nested object of mappings.</summary>
public abstract record Mapping;

/// <summary><c>{"ref": "&lt;root&gt;[.field…]"}</c>; root is <c>input</c>, a step name or <c>failure</c>.</summary>
public sealed record RefMapping(string Ref) : Mapping
{
    public string Root => Ref.Split('.')[0];
    public IReadOnlyList<string> Path => Ref.Split('.')[1..];
}

/// <summary><c>{"const": &lt;json&gt;}</c>.</summary>
public sealed record ConstMapping(JsonElement Value) : Mapping;

/// <summary>An object built field by field.</summary>
public sealed record ObjectMapping(IReadOnlyDictionary<string, Mapping> Fields) : Mapping;

/// <summary>A failure handler; <see cref="Steps"/> null = any step.</summary>
public sealed record ManifestFailureHandler(StepDescriptor Step, IReadOnlyList<string>? Steps);

/// <summary>Root names with special meaning in refs.</summary>
public static class ManifestRoots
{
    public const string Input = "input";
    public const string Failure = "failure";
    public const string FailureContextModel = "FailureContext";
}

public static class StepKinds
{
    public const string Custom = "custom";
    public const string Script = "script";

    /// <summary>The kinds manifest schema 2.0 allows. A new SDK library kind needs a service update.</summary>
    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal) { Custom, Script };
}
