using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Versions;

namespace Olve.Pipelines.Pipelines.Scheduling;

/// <summary>
/// A manifest's stages flattened into execution order (spec §3.6): nested <c>when</c> blocks become
/// conditions carried by each stage they enclose (outermost first). The scheduler walks this list.
/// </summary>
public sealed record RunPlan(Manifest Manifest, IReadOnlyList<PlanStage> Stages)
{
    public static RunPlan From(Manifest manifest)
    {
        var stages = new List<PlanStage>();
        Flatten(manifest.Stages, [], stages);
        return new RunPlan(manifest, stages);
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

    private static void Flatten(IReadOnlyList<ManifestStage> source, IReadOnlyList<WhenCondition> conditions, List<PlanStage> target)
    {
        foreach (var stage in source)
        {
            switch (stage)
            {
                case StepsStage steps:
                    target.Add(new PlanStage(target.Count, conditions, steps.Steps));
                    break;
                case WhenStage block:
                    var inner = block.When.Count == 0
                        ? conditions
                        : [.. conditions, new WhenCondition(block.When)];
                    Flatten(block.Stages, inner, target);
                    break;
            }
        }
    }
}

/// <summary>One barrier stage: its steps run in parallel when every enclosing condition matches.</summary>
public sealed record PlanStage(int Index, IReadOnlyList<WhenCondition> Conditions, IReadOnlyList<StepDescriptor> Steps)
{
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
