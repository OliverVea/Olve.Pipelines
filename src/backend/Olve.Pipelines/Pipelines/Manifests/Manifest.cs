using System.Text.Json;

namespace Olve.Pipelines.Pipelines.Manifests;

// C# mirror of the v2 manifest (src/sdk/manifest.schema.json, generated from src/sdk/src/describe/manifest-schema.ts).
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

    /// <summary>Config entries the version declares (empty when the manifest has no <c>config</c>).</summary>
    public IReadOnlyList<ManifestConfigEntry> Config { get; init; } = [];

    /// <summary>Retention overrides the pipeline declares (null when the manifest has no <c>retention</c>).</summary>
    public ManifestRetention? Retention { get; init; }

    /// <summary>Hash of the whole build; steps then carry no <see cref="StepDescriptor.HandlerHash"/>.</summary>
    public string? CodeHash { get; init; }
}

/// <summary>Per-pipeline retention: each set field replaces the instance's <c>Retention:*</c> value of the same meaning.</summary>
public sealed record ManifestRetention(long? KeepLatestRuns, long? MinRunAgeDays, long? BuildGraceDays);

/// <summary>A typed config entry: one top-level key of the pipeline's config document.</summary>
/// <param name="Schema">The entry's JSON Schema (input view: fields with defaults are optional and list their <c>default</c>).</param>
/// <param name="Required">False when the entry may be absent (optional, or it has a default).</param>
public sealed record ManifestConfigEntry(string Name, string Description, bool Required, JsonElement Schema);

/// <summary>Harness identity (already folded into the code and handler hashes).</summary>
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

/// <summary>Inbound HTTP delivery to <c>/api/hooks/{pipeline}/{Name}</c>, authenticated by <see cref="Verify"/>.</summary>
public sealed record WebhookTrigger(string Name, WebhookTriggerVerify Verify) : ManifestTrigger(Name);

/// <summary>How a webhook trigger authenticates deliveries; <see cref="Secret"/> names a pipeline secret.</summary>
public abstract record WebhookTriggerVerify(string Secret);

/// <summary>HMAC-SHA256 of the raw body keyed by the secret, hex, in <see cref="Header"/> after <see cref="Prefix"/>.</summary>
public sealed record HmacSha256TriggerVerify(string Secret, string Header, string Prefix) : WebhookTriggerVerify(Secret);

/// <summary><c>Authorization: Bearer &lt;secret&gt;</c>.</summary>
public sealed record BearerTriggerVerify(string Secret) : WebhookTriggerVerify(Secret);

public abstract record ManifestStage;

/// <summary>A barrier stage: its steps run in parallel.</summary>
public sealed record StepsStage(IReadOnlyList<StepDescriptor> Steps) : ManifestStage;

/// <summary>A conditional block: pipeline-input field → accepted values (string/number/boolean). Empty = always.</summary>
public sealed record WhenStage(
    IReadOnlyDictionary<string, IReadOnlyList<JsonElement>> When,
    IReadOnlyList<ManifestStage> Stages) : ManifestStage;

/// <summary>
/// A branch on config: <see cref="Select"/> references a discriminated union in a config entry
/// (<c>target.prod</c>); the run's config snapshot picks the case whose key is the union's
/// <see cref="Discriminator"/> value. Steps of different cases may share a name (only one exists in any run).
/// </summary>
public sealed record SelectStage(
    string Select,
    string Discriminator,
    IReadOnlyDictionary<string, IReadOnlyList<ManifestStage>> Cases) : ManifestStage
{
    /// <summary>The condition a case stands for, as recorded on narrowed config references: <c>target.prod.kind=ssh</c>.</summary>
    public string Condition(string value) => $"{Select}.{Discriminator}={value}";
}

/// <summary>A step. <see cref="Description"/> is display-only (not part of the step's content hash).</summary>
public sealed record StepDescriptor(
    string Name,
    string Description,
    string Kind,
    string Image,
    IReadOnlyDictionary<string, InputType> InputTypes,
    IReadOnlyDictionary<string, Mapping> Input,
    string? Output,
    IReadOnlyList<string> Secrets,
    string? HandlerHash,
    long? TimeoutSeconds,
    long? Retries,
    IReadOnlyDictionary<string, JsonElement>? Config)
{
    /// <summary>Config entries delivered to the step as <c>ctx.config</c> (sorted; empty = none).</summary>
    public IReadOnlyList<string> ConfigEntries { get; init; } = [];

    /// <summary>The step pod gets the instance's step ServiceAccount token (<c>Kubernetes:StepServiceAccount</c>), if it grants one.</summary>
    public bool ClusterAccess { get; init; }

    /// <summary>Inputs whose values come from outside the run; the step waits for them before it runs.</summary>
    public IReadOnlyDictionary<string, ExternalInput> ExternalInputs { get; init; } = new Dictionary<string, ExternalInput>();
}

/// <summary>An input answered from outside the run, validated against <see cref="Model"/>'s input view.</summary>
public sealed record ExternalInput(string Model, string Title, string Description, IReadOnlyList<ExternalSource> From);

/// <summary>Who or what may supply an external input's value.</summary>
public abstract record ExternalSource
{
    /// <summary>A signed-in user, in the UI or with <c>pl input</c>.</summary>
    public sealed record Person : ExternalSource;

    /// <summary>Another system, posting to the run's callback URL for the input (<c>ctx.inputUrl</c>).</summary>
    public sealed record Callback : ExternalSource;

    /// <summary>
    /// The service GETs <see cref="Url"/> every <see cref="IntervalSeconds"/> while the step waits; the value at
    /// <see cref="ValuePath"/> answers once it validates. <c>{run.id}</c> and <c>{run.number}</c> in the URL are filled in.
    /// </summary>
    public sealed record Poll(string Url, string ValuePath, int IntervalSeconds) : ExternalSource;

    public string Kind => this switch
    {
        Person => "person",
        Callback => "callback",
        Poll => "poll",
        _ => throw new InvalidOperationException($"Unknown external input source {GetType().Name}."),
    };
}

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
    public const string GitHubSource = "gitHubSource";
    public const string ImageBuild = "imageBuild";
    public const string HelmDeployOverSsh = "helmDeployOverSsh";
    public const string HelmDeployInCluster = "helmDeployInCluster";
    public const string ComposeDeploy = "composeDeploy";
}
