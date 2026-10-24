using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Versions;

namespace Olve.Pipelines.Pipelines.Scheduling;

/// <summary>
/// A manifest's stages flattened into execution order (spec §3.6): nested <c>when</c> blocks become
/// conditions carried by each stage they enclose (outermost first). The scheduler walks this list.
/// <c>select</c> blocks are resolved by a config (<see cref="ForConfig"/>): only the selected case's stages
/// are in the plan, which is the version's effective pipeline for that config. A plan built without
/// config (<see cref="From"/>) leaves every select out; runs always use their effective plan
/// (<see cref="RunPlanCache.ForRun"/>).
/// </summary>
public sealed record RunPlan(Manifest Manifest, IReadOnlyList<PlanStage> Stages)
{
    /// <summary>The selects of the manifest that were resolved, as <c>select → chosen value</c> (empty without selects).</summary>
    public IReadOnlyDictionary<string, string> Selected { get; init; } = new Dictionary<string, string>();

    public static RunPlan From(Manifest manifest)
    {
        var stages = new List<PlanStage>();
        Flatten(manifest.Stages, [], [], stages, config: null, selected: null, problems: null);
        return new RunPlan(manifest, stages);
    }

    /// <summary>The effective plan for <paramref name="config"/> (a run's effective config): each select resolved to its case.</summary>
    public static Result<RunPlan> ForConfig(Manifest manifest, JsonElement config)
    {
        var stages = new List<PlanStage>();
        var selected = new Dictionary<string, string>(StringComparer.Ordinal);
        var problems = new List<string>();
        Flatten(manifest.Stages, [], [], stages, config, selected, problems);
        if (problems.Count > 0)
            return new ResultProblem("{0}", string.Join("; ", problems));
        return new RunPlan(manifest, stages) { Selected = selected };
    }

    /// <summary><paramref name="stages"/> with each select replaced by its selected case's stages (by <see cref="Selected"/>).</summary>
    public static IReadOnlyList<ManifestStage> Effective(IReadOnlyList<ManifestStage> stages, IReadOnlyDictionary<string, string> selected)
    {
        var result = new List<ManifestStage>();
        foreach (var stage in stages)
        {
            switch (stage)
            {
                case WhenStage w:
                    result.Add(w with { Stages = Effective(w.Stages, selected) });
                    break;
                case SelectStage s when selected.TryGetValue(s.Select, out var value) && s.Cases.TryGetValue(value, out var chosen):
                    result.AddRange(Effective(chosen, selected));
                    break;
                case SelectStage:
                    break;
                default:
                    result.Add(stage);
                    break;
            }
        }
        return result;
    }

    /// <summary>True when the manifest branches on config anywhere.</summary>
    public static bool HasSelect(IReadOnlyList<ManifestStage> stages) =>
        stages.Any(s => s is SelectStage || (s is WhenStage w && HasSelect(w.Stages)));

    /// <summary>The value of <paramref name="select"/>'s discriminator in <paramref name="config"/>, or null.</summary>
    public static string? SelectedValue(SelectStage select, JsonElement config)
    {
        var cur = config;
        foreach (var seg in select.Select.Split('.').Append(select.Discriminator))
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(seg, out cur)) return null;
        }
        return cur.ValueKind == JsonValueKind.String ? cur.GetString() : null;
    }

    /// <summary>Why <paramref name="stepName"/> is not part of a run with <paramref name="runInput"/> (its <c>when</c>), or null.</summary>
    public string? ExcludedReason(string stepName, JsonElement runInput) =>
        TryGetStep(stepName, out var stage, out _) ? stage.EvaluateWhen(runInput) : null;

    public bool TryGetStep(string stepName, [NotNullWhen(true)] out PlanStage? stage, [NotNullWhen(true)] out StepDescriptor? step)
    {
        foreach (var s in Stages)
        {
            foreach (var candidate in s.Steps)
            {
                if (candidate.Name != stepName) continue;
                stage = s;
                step = candidate;
                return true;
            }
        }

        stage = null;
        step = null;
        return false;
    }

    private static void Flatten(
        IReadOnlyList<ManifestStage> source, IReadOnlyList<WhenCondition> conditions, IReadOnlyList<string> cases, List<PlanStage> target,
        JsonElement? config, Dictionary<string, string>? selected, List<string>? problems)
    {
        foreach (var stage in source)
        {
            switch (stage)
            {
                case StepsStage steps:
                    target.Add(new PlanStage(target.Count, conditions, steps.Steps) { Cases = cases });
                    break;
                case WhenStage block:
                    var inner = block.When.Count == 0
                        ? conditions
                        : [.. conditions, new WhenCondition(block.When)];
                    Flatten(block.Stages, inner, cases, target, config, selected, problems);
                    break;
                case SelectStage select when config is { } c:
                    var value = SelectedValue(select, c);
                    if (value is null || !select.Cases.TryGetValue(value, out var chosen))
                    {
                        problems?.Add($"select '{select.Select}': the config's {select.Discriminator} ({value ?? "unset"}) is not one of its cases ({string.Join(", ", select.Cases.Keys)})");
                        break;
                    }
                    selected?.TryAdd(select.Select, value);
                    Flatten(chosen, conditions, [.. cases, select.Condition(value)], target, config, selected, problems);
                    break;
            }
        }
    }
}

/// <summary>One barrier stage: its steps run in parallel when every enclosing condition matches.</summary>
public sealed record PlanStage(int Index, IReadOnlyList<WhenCondition> Conditions, IReadOnlyList<StepDescriptor> Steps)
{
    /// <summary>The select cases the stage is in (<c>target.prod.kind=ssh</c>); the harness picks a same-named step's variant by them.</summary>
    public IReadOnlyList<string> Cases { get; init; } = [];

    /// <summary>The pipeline-input fields the stage's conditions read.</summary>
    public IEnumerable<string> ConditionFields => Conditions.SelectMany(c => c.Fields.Keys);

    /// <summary>
    /// Evaluates the enclosing conditions against the run input (outermost first, fields in ordinal
    /// order). Returns null when all match, else the skip reason of the first mismatch.
    /// </summary>
    public string? EvaluateWhen(JsonElement runInput)
    {
        foreach (var condition in Conditions)
        {
            if (condition.Evaluate(runInput) is { } reason)
                return reason;
        }

        return null;
    }
}

/// <summary>A <c>when</c> block: pipeline-input field → accepted values (DeepEquals). Empty matches always.</summary>
public sealed record WhenCondition(IReadOnlyDictionary<string, IReadOnlyList<JsonElement>> Fields)
{
    public string? Evaluate(JsonElement runInput)
    {
        foreach (var (field, allowed) in Fields.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var matches = runInput.ValueKind == JsonValueKind.Object
                          && runInput.TryGetProperty(field, out var value)
                          && allowed.Any(a => JsonElement.DeepEquals(a, value));
            if (!matches)
                return $"when {field} ∉ [{string.Join(", ", allowed.Select(Format))}]";
        }

        return null;
    }

    private static string Format(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        _ => value.GetRawText(),
    };
}

/// <summary>
/// Parsed manifests and plans per pipeline version (versions are immutable, so entries never go stale).
/// Parses the version's stored canonical manifest — <see cref="Manifest"/> is never (de)serialized via
/// System.Text.Json source generation. Singleton (it is a cache).
/// </summary>
public class RunPlanCache(PipelineVersionService versions)
{
    private readonly ConcurrentDictionary<Id<PipelineVersion>, Result<RunPlan>> _plans = new();
    private readonly ConcurrentDictionary<(Id<PipelineVersion> Version, int Snapshot), Result<RunPlan>> _effective = new();

    /// <summary>
    /// The run's effective plan: its version with each select resolved by the run's pinned config. Keyed on the
    /// version and the config snapshot, both immutable.
    /// </summary>
    public Result<RunPlan> ForRun(Runs.Run run)
    {
        if (Get(run.VersionId).TryPickProblems(out var problems, out var plan))
            return problems;
        if (!RunPlan.HasSelect(plan.Manifest.Stages))
            return plan;
        if (run.ConfigSnapshot is not { } snapshot || run.Config is null)
            return new ResultProblem("Run #{0} has no config snapshot, but its version branches on config.", run.Number);
        return _effective.GetOrAdd((run.VersionId, snapshot), _ => ForConfig(plan.Manifest, run.Config));
    }

    /// <summary>
    /// The effective plan of a version under the pipeline's current config (its latest snapshot): what new runs would
    /// execute. Without a select, or without a usable config, the version's plan.
    /// </summary>
    public Result<RunPlan> ForCurrentConfig(Id<PipelineVersion> versionId, Configs.ConfigSnapshotService snapshots)
    {
        if (Get(versionId).TryPickProblems(out var problems, out var plan))
            return problems;
        if (!RunPlan.HasSelect(plan.Manifest.Stages) || !versions.TryGet(versionId, out var version)
            || !snapshots.TryGetLatest(version.PipelineId, out var latest))
            return plan;
        return _effective.GetOrAdd((versionId, latest.Number), _ =>
            Configs.ConfigValidation.Effective(plan.Manifest, snapshots.Document(latest)).TryPickProblems(out var invalid, out var effective)
                ? Result.Failure<RunPlan>(invalid)
                : ForConfig(plan.Manifest, effective.ToJsonString()));
    }

    /// <summary>The effective plan of <paramref name="manifest"/> for an effective config document (JSON text).</summary>
    public static Result<RunPlan> ForConfig(Manifest manifest, string effectiveConfig)
    {
        using var doc = JsonDocument.Parse(effectiveConfig);
        return RunPlan.ForConfig(manifest, doc.RootElement.Clone());
    }

    public Result<RunPlan> Get(Id<PipelineVersion> versionId)
    {
        if (_plans.TryGetValue(versionId, out var cached))
            return cached;

        if (!versions.TryGet(versionId, out var version))
            return new NotFoundProblem("Pipeline version '{0}' not found.", versionId);

        var parsed = ManifestParser.Parse(version.Manifest);
        Result<RunPlan> plan = parsed.TryPickProblems(out var problems, out var manifest)
            ? problems.Prepend("Manifest of version {0} does not parse.", version.Number)
            : RunPlan.From(manifest);
        return _plans.GetOrAdd(versionId, plan);
    }
}
