using System.Text.Json;
using System.Text.Json.Nodes;
using Olve.Pipelines.Pipelines.Manifests;

namespace Olve.Pipelines.Pipelines.Configs;

/// <summary>A default the target version applies differently from the active one, which the document relies on.</summary>
/// <param name="Path">The field, e.g. <c>telemetry.exporter</c>.</param>
/// <param name="From">The active version's default (JSON).</param>
/// <param name="To">The target version's default (JSON).</param>
public sealed record ChangedDefault(string Path, string From, string To);

/// <summary>
/// A config document checked against one version.
/// </summary>
/// <param name="Problems">Validation problems (<c>config.auth.clientId: is required</c>); empty when valid.</param>
/// <param name="ReferencedSecrets">Secrets the effective config references.</param>
/// <param name="MissingSecrets">Referenced secrets that are not set (all of them when the secret state is unknown).</param>
/// <param name="Effective">The document with the version's defaults applied, only its declared entries (null when invalid).</param>
public sealed record ConfigCheck(
    int VersionNumber,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> ReferencedSecrets,
    IReadOnlyList<string> MissingSecrets,
    IReadOnlyList<ChangedDefault> ChangedDefaults,
    JsonObject? Effective)
{
    public bool Valid => Problems.Count == 0;

    /// <summary>The activation gate: every required entry present and valid, every referenced secret set.</summary>
    public bool Satisfied => Valid && MissingSecrets.Count == 0;

    /// <summary>Why the version can't activate, one line per reason.</summary>
    public IReadOnlyList<string> Reasons =>
        Problems.Select(p => $"config {p}").Concat(MissingSecrets.Select(s => $"secret {s} not set")).ToList();
}

/// <summary>Validates config documents against a version's config schema. Pure.</summary>
public static class ConfigValidation
{
    public const string Root = "config";

    /// <summary>
    /// The document with <paramref name="manifest"/>'s defaults applied, keeping only the entries it declares
    /// (keys it doesn't declare are ignored, so config for a future version can be saved first).
    /// </summary>
    public static Result<JsonObject> Effective(Manifest manifest, JsonObject document, List<AppliedDefault>? defaults = null)
    {
        var projected = new JsonObject();
        foreach (var entry in manifest.Config)
        {
            if (document.TryGetPropertyValue(entry.Name, out var value))
                projected[entry.Name] = value?.DeepClone();
        }

        var validated = JsonSchemaValidator.Validate(DocumentSchema(manifest), projected, Root, defaults);
        if (validated.TryPickProblems(out var problems, out var result))
            return Result.Failure<JsonObject>(problems);
        return result as JsonObject ?? new JsonObject();
    }

    /// <summary>
    /// Checks <paramref name="document"/> against <paramref name="target"/>. <paramref name="setSecrets"/> null =
    /// unknown (every referenced secret counts as missing). With <paramref name="active"/>, lists the defaults
    /// the target changes that the document relies on.
    /// </summary>
    public static ConfigCheck Check(Manifest target, int targetNumber, JsonObject document, IReadOnlySet<string>? setSecrets, Manifest? active = null)
    {
        var targetDefaults = new List<AppliedDefault>();
        var validated = Effective(target, document, targetDefaults);

        var changed = new List<ChangedDefault>();
        var activeDefaults = new List<AppliedDefault>();
        if (active is not null && !Effective(active, document, activeDefaults).TryPickProblems(out _))
        {
            var before = activeDefaults.GroupBy(d => d.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
            foreach (var d in targetDefaults.DistinctBy(d => d.Path))
            {
                if (before.TryGetValue(d.Path, out var old) && !JsonNode.DeepEquals(old, d.Value))
                    changed.Add(new ChangedDefault(d.Path[(Root.Length + 1)..], Text(old), Text(d.Value)));
            }
        }

        if (validated.TryPickProblems(out var problems, out var effective))
            return new ConfigCheck(targetNumber, problems.Select(p => p.ToDisplayString()).ToList(), [], [], changed, null);

        var referenced = ReferencedSecrets(target, effective);
        var missing = referenced.Where(s => setSecrets is null || !setSecrets.Contains(s)).ToList();
        return new ConfigCheck(targetNumber, [], referenced, missing, changed, effective);
    }

    /// <summary>Secrets referenced by the effective config (<c>{secret: NAME}</c> where the entry's schema allows a secretRef to NAME).</summary>
    public static IReadOnlyList<string> ReferencedSecrets(Manifest manifest, JsonObject effective)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest.Config)
        {
            if (effective.TryGetPropertyValue(entry.Name, out var value))
                names.UnionWith(ConfigSecretRefs.Used(value, ConfigSecretRefs.Declared(entry.Schema)));
        }
        return names.ToList();
    }

    /// <summary>The step's slice of a run's effective config (<c>ctx.config</c>).</summary>
    public static JsonObject ForStep(StepDescriptor step, JsonObject effective)
    {
        var slice = new JsonObject();
        foreach (var name in step.ConfigEntries)
        {
            if (effective.TryGetPropertyValue(name, out var value))
                slice[name] = value?.DeepClone();
        }
        return slice;
    }

    /// <summary>An object schema with one property per entry; required entries are required, other keys ignored.</summary>
    public static JsonElement DocumentSchema(Manifest manifest) => ManifestWriter.ToElement(w =>
    {
        w.WriteStartObject();
        w.WriteString("type", "object");
        w.WriteStartObject("properties");
        foreach (var entry in manifest.Config)
        {
            w.WritePropertyName(entry.Name);
            entry.Schema.WriteTo(w);
        }
        w.WriteEndObject();
        w.WriteStartArray("required");
        foreach (var entry in manifest.Config.Where(e => e.Required))
            w.WriteStringValue(entry.Name);
        w.WriteEndArray();
        w.WriteEndObject();
    });

    private static string Text(JsonNode? node) => node?.ToJsonString() ?? "null";
}

/// <summary>How a step receives its config: under the reserved <c>$config</c> key of its <c>input.json</c>.</summary>
public static class ConfigDelivery
{
    public const string InputKey = "$config";

    /// <summary>
    /// <paramref name="inputJson"/> plus the step's slice of the run's effective config. Unchanged for a step
    /// that declares no config, so binaries built before config existed never see the key.
    /// </summary>
    public static string WithConfig(string inputJson, StepDescriptor step, string? runConfig)
    {
        if (step.ConfigEntries.Count == 0)
            return inputJson;
        var input = JsonNode.Parse(inputJson) as JsonObject ?? new JsonObject();
        var effective = runConfig is null ? new JsonObject() : JsonNode.Parse(runConfig) as JsonObject ?? new JsonObject();
        input[InputKey] = ConfigValidation.ForStep(step, effective);
        return input.ToJsonString();
    }
}
