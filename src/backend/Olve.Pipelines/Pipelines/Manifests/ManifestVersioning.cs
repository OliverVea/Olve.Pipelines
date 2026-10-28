using System.Text.Json;

namespace Olve.Pipelines.Pipelines.Manifests;

/// <summary>An entity's version number and the content hash it was assigned for.</summary>
public sealed record EntityVersion(int Version, string ContentHash);

/// <summary>Model and step versions keyed by name (the entity identity).</summary>
public sealed record EntityVersionMap(
    IReadOnlyDictionary<string, EntityVersion> Models,
    IReadOnlyDictionary<string, EntityVersion> Steps)
{
    public static EntityVersionMap Empty { get; } = new(
        new Dictionary<string, EntityVersion>(StringComparer.Ordinal),
        new Dictionary<string, EntityVersion>(StringComparer.Ordinal));
}

/// <summary>
/// Result of <see cref="ManifestVersioning.Compute"/>.
/// <see cref="Current"/> = the entities of this manifest; <see cref="History"/> = previous history with
/// <see cref="Current"/> merged in (retired entities keep their last entry, so a re-added name resumes
/// its numbering). Persist <see cref="History"/> and pass it to the next Compute.
/// </summary>
public sealed record ManifestVersions(
    string ManifestHash,
    string CanonicalManifest,
    EntityVersionMap Current,
    EntityVersionMap History,
    IReadOnlyList<string> ChangedModels,
    IReadOnlyList<string> ChangedSteps);

/// <summary>
/// Entity versioning (design §6.1, §6.3). Pure: manifest + previous history → hashes and version numbers.
/// A model/step keeps its number while its content hash is unchanged, else gets last + 1 (first seen: 1).
/// A step's content references models by <c>Name.Version</c>, so a model change ripples to its consumers.
/// Stage placement and <c>when</c> wrapping are pipeline changes, not step changes.
/// </summary>
public static class ManifestVersioning
{
    /// <summary><c>&lt;name&gt;.&lt;version&gt;</c>, e.g. <c>deploy-beta.3</c>.</summary>
    public static string EntityId(string name, int version) => $"{name}.{version}";

    public static ManifestVersions Compute(EntityVersionMap? history, Manifest manifest)
    {
        history ??= EntityVersionMap.Empty;
        var canonical = ManifestWriter.ToCanonicalJson(manifest);

        var currentModels = new Dictionary<string, EntityVersion>(StringComparer.Ordinal);
        var changedModels = new List<string>();
        foreach (var (name, schemas) in manifest.Models.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            currentModels[name] = Next(history.Models, name, ModelContentHash(schemas), changedModels);

        var modelVersions = currentModels.ToDictionary(kv => kv.Key, kv => kv.Value.Version, StringComparer.Ordinal);
        var currentSteps = new Dictionary<string, EntityVersion>(StringComparer.Ordinal);
        var changedSteps = new List<string>();
        // A name has one version: select cases may give it several variants, hashed together (sorted), so a
        // change to any of them bumps it. A failure handler attached several times is one variant.
        foreach (var group in AllSteps(manifest).GroupBy(s => s.Name, StringComparer.Ordinal))
        {
            var hashes = group.Select(s => StepContentHash(s, modelVersions, manifest.Config)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var hash = hashes.Count == 1 ? hashes[0] : CanonicalJson.HashText(string.Join(' ', hashes));
            currentSteps[group.Key] = Next(history.Steps, group.Key, hash, changedSteps);
        }

        var current = new EntityVersionMap(currentModels, currentSteps);
        var merged = new EntityVersionMap(Merge(history.Models, currentModels), Merge(history.Steps, currentSteps));
        return new ManifestVersions(CanonicalJson.HashText(canonical), canonical, current, merged, changedModels, changedSteps);
    }

    /// <summary>sha256 of the canonical manifest — equal to sha256 of describe's output bytes.</summary>
    public static string ManifestHash(Manifest manifest) => CanonicalJson.HashText(ManifestWriter.ToCanonicalJson(manifest));

    /// <summary>Model content = canonical JSON of its schema views.</summary>
    public static string ModelContentHash(ModelSchemas schemas) => CanonicalJson.Hash(ManifestWriter.ToElement(schemas));

    /// <summary>
    /// Step content = kind, image, input types (models as <c>Name.Version</c>), mappings, output model version,
    /// secrets (as a set), handler hash, config, timeout, retries.
    /// </summary>
    public static string StepContentHash(StepDescriptor s, IReadOnlyDictionary<string, int> modelVersions, IReadOnlyList<ManifestConfigEntry>? config = null)
    {
        string Versioned(string model) => modelVersions.TryGetValue(model, out var v) ? EntityId(model, v) : model;
        var content = ManifestWriter.ToElement(w =>
        {
            w.WriteStartObject();
            w.WriteString("kind", s.Kind);
            w.WriteString("image", s.Image);
            w.WriteStartObject("inputTypes");
            foreach (var (key, type) in s.InputTypes)
            {
                w.WriteStartObject(key);
                switch (type)
                {
                    case ModelInputType mt:
                        w.WriteString("model", Versioned(mt.Model));
                        w.WriteBoolean("optional", mt.Optional);
                        break;
                    case SchemaInputType st:
                        w.WritePropertyName("schema");
                        ManifestWriter.Write(w, st.Schema);
                        break;
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteStartObject("input");
            foreach (var (key, mapping) in s.Input)
            {
                w.WritePropertyName(key);
                ManifestWriter.Write(w, mapping);
            }
            w.WriteEndObject();
            if (s.Output is null) w.WriteNull("output");
            else w.WriteString("output", Versioned(s.Output));
            w.WriteStartArray("secrets");
            foreach (var secret in s.Secrets.Distinct().Order(StringComparer.Ordinal)) w.WriteStringValue(secret);
            w.WriteEndArray();
            w.WriteString("handlerHash", s.HandlerHash);
            if (s.TimeoutSeconds is { } t) w.WriteNumber("timeoutSeconds", t);
            if (s.Retries is { } r) w.WriteNumber("retries", r);
            if (s.Config is not null)
            {
                w.WriteStartObject("config");
                foreach (var (k, v) in s.Config)
                {
                    w.WritePropertyName(k);
                    v.WriteTo(w);
                }
                w.WriteEndObject();
            }
            ManifestWriter.WriteExternalInputs(w, s, Versioned);
            if (s.ConfigEntries.Count > 0)
            {
                // Entry schemas count, so a changed config schema bumps the steps that receive it.
                w.WriteStartObject("configEntries");
                foreach (var name in s.ConfigEntries)
                {
                    w.WritePropertyName(name);
                    if (config?.FirstOrDefault(c => c.Name == name) is { } entry) ManifestWriter.Write(w, entry);
                    else w.WriteNullValue();
                }
                w.WriteEndObject();
            }
            if (s.ClusterAccess) w.WriteBoolean("clusterAccess", true);
            w.WriteEndObject();
        });
        return CanonicalJson.Hash(content);
    }

    /// <summary>Stage steps in manifest order (depth-first through when blocks), then failure handler steps.</summary>
    public static IEnumerable<StepDescriptor> AllSteps(Manifest manifest)
    {
        foreach (var s in StageSteps(manifest.Stages)) yield return s;
        foreach (var fh in manifest.FailureHandlers) yield return fh.Step;
    }

    private static IEnumerable<StepDescriptor> StageSteps(IReadOnlyList<ManifestStage> stages)
    {
        foreach (var stage in stages)
        {
            switch (stage)
            {
                case StepsStage ss:
                    foreach (var s in ss.Steps) yield return s;
                    break;
                case WhenStage ws:
                    foreach (var s in StageSteps(ws.Stages)) yield return s;
                    break;
                case SelectStage sel:
                    foreach (var caseStages in sel.Cases.Values)
                        foreach (var s in StageSteps(caseStages)) yield return s;
                    break;
            }
        }
    }

    private static EntityVersion Next(IReadOnlyDictionary<string, EntityVersion> history, string name, string hash, List<string> changed)
    {
        if (history.TryGetValue(name, out var last))
        {
            if (last.ContentHash == hash) return last;
            changed.Add(name);
            return new EntityVersion(last.Version + 1, hash);
        }
        changed.Add(name);
        return new EntityVersion(1, hash);
    }

    private static Dictionary<string, EntityVersion> Merge(IReadOnlyDictionary<string, EntityVersion> history, Dictionary<string, EntityVersion> current)
    {
        var merged = new Dictionary<string, EntityVersion>(history, StringComparer.Ordinal);
        foreach (var (k, v) in current) merged[k] = v;
        return merged;
    }
}
