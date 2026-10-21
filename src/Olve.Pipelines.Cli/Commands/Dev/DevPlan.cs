using System.Text.Json.Nodes;

namespace Olve.Pipelines.Cli.Commands.Dev;

/// <summary>A step in pipeline order with its stage number (1-based) and the <c>when</c> conditions guarding it.</summary>
public sealed record PlannedStep(DevStep Step, int Stage, IReadOnlyList<WhenCondition> Conditions);

/// <summary>Flattens the manifest's stage tree and evaluates <c>when</c> blocks against the pipeline input.</summary>
public static class DevPlan
{
    /// <summary>All stage steps in order (failure handlers excluded). Stages inside <c>when</c> blocks keep their place.</summary>
    public static IReadOnlyList<PlannedStep> Flatten(DevManifest manifest)
    {
        var result = new List<PlannedStep>();
        var stage = 0;
        Walk(manifest.Stages, []);
        return result;

        void Walk(IReadOnlyList<DevStageNode> nodes, IReadOnlyList<WhenCondition> conditions)
        {
            foreach (var node in nodes)
            {
                switch (node)
                {
                    case DevStage s:
                        stage++;
                        result.AddRange(s.Steps.Select(step => new PlannedStep(step, stage, conditions)));
                        break;
                    case DevWhenBlock w:
                        Walk(w.Nodes, [.. conditions, .. w.Conditions]);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Null when every condition matches <paramref name="input"/>; otherwise the skip reason of the
    /// first one that does not (e.g. <c>when deployTo in ["prod"] (deployTo = "beta")</c>).
    /// </summary>
    public static string? WhenMismatch(IReadOnlyList<WhenCondition> conditions, JsonObject? input)
    {
        foreach (var condition in conditions)
        {
            JsonNode? actual = null;
            var present = input is not null && input.TryGetPropertyValue(condition.Field, out actual);
            if (present && condition.Values.Any(v => JsonNode.DeepEquals(v, actual)))
                continue;

            var shown = present ? actual?.ToJsonString() ?? "null" : "unset";
            return $"when {condition} did not match ({condition.Field} = {shown})";
        }

        return null;
    }

    /// <summary>
    /// Applies <c>--until</c> (run through the stage containing that step) and <c>--only</c>
    /// (run just these steps, in pipeline order). Unknown names are problems.
    /// </summary>
    public static Result<IReadOnlyList<PlannedStep>> Select(
        IReadOnlyList<PlannedStep> plan, string? until, IReadOnlyList<string>? only)
    {
        var known = plan.Select(p => p.Step.Name).ToHashSet(StringComparer.Ordinal);
        var problems = new List<ResultProblem>();

        if (until is not null && !known.Contains(until))
            problems.Add(new ResultProblem("--until: unknown step '{0}' (steps: {1}).", until, string.Join(", ", known)));
        foreach (var name in only ?? [])
        {
            if (!known.Contains(name))
                problems.Add(new ResultProblem("--only: unknown step '{0}' (steps: {1}).", name, string.Join(", ", known)));
        }

        if (problems.Count > 0)
            return new ResultProblemCollection(problems);

        IEnumerable<PlannedStep> selected = plan;
        if (until is not null)
        {
            var lastStage = plan.First(p => p.Step.Name == until).Stage;
            selected = selected.Where(p => p.Stage <= lastStage);
        }

        if (only is { Count: > 0 })
            selected = selected.Where(p => only.Contains(p.Step.Name));

        return Result.Success<IReadOnlyList<PlannedStep>>(selected.ToList());
    }
}
