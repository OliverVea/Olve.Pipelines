using System.Text.Json.Nodes;

namespace Olve.Pipelines.Cli.Commands.Dev;

/// <summary>The outcome of building one step's <c>input.json</c> from the local pool.</summary>
public sealed record StepInputAssembly(
    JsonObject Input,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> SkippedBecause)
{
    /// <summary>True when the step can run: no problems and no required input blocked by a skipped step.</summary>
    public bool Ready => Problems.Count == 0 && SkippedBecause.Count == 0;
}

/// <summary>
/// Builds a step's <c>input.json</c> the way the service does (sdk README, "Files"): exactly the
/// step's input keys, each resolved from its manifest mapping —
/// <c>{"ref": "input.x"}</c> against the pipeline input, <c>{"ref": "step[.path]"}</c> against the
/// pool, <c>{"const": …}</c> verbatim, nested objects recursively. A key whose ref root step was
/// skipped (or never ran) is left out when the input is <c>optional</c>; for a required input a
/// skipped root skips this step too (chained reason), and a root that never ran is a problem.
/// </summary>
public static class StepInputAssembler
{
    public static StepInputAssembly Assemble(DevStep step, DevPool pool)
    {
        var input = new JsonObject();
        var problems = new List<string>();
        var skipped = new List<string>();

        foreach (var (key, mapping) in step.Input)
        {
            var unavailable = new List<(string Root, PoolEntry? Entry)>();
            var value = Resolve(mapping, pool, unavailable, problems);

            if (unavailable.Count == 0)
            {
                if (value.Present)
                    input[key] = value.Node;
                continue;
            }

            if (step.OptionalInputs.GetValueOrDefault(key))
                continue; // maybe-skipped input: leave the key out

            foreach (var (root, entry) in unavailable.DistinctBy(u => u.Root))
            {
                switch (entry?.Status)
                {
                    case PoolEntry.Skipped:
                        skipped.Add($"needs '{root}', which was skipped ({entry.Reason})");
                        break;
                    case PoolEntry.Failed:
                        problems.Add($"input '{key}' needs the output of '{root}', whose last local run failed; rerun `pl dev run {root}`");
                        break;
                    default:
                        problems.Add($"input '{key}' needs the output of '{root}', which is not in the local pool; run `pl dev run {root}` first (or pass --input-file)");
                        break;
                }
            }
        }

        return new StepInputAssembly(input, problems, skipped);
    }

    private readonly record struct Resolved(bool Present, JsonNode? Node)
    {
        public static readonly Resolved Absent = new(false, null);
    }

    private static Resolved Resolve(
        JsonNode? mapping,
        DevPool pool,
        List<(string Root, PoolEntry? Entry)> unavailable,
        List<string> problems)
    {
        if (mapping is JsonObject obj)
        {
            if (obj.Count == 1 && DevManifest.Str(obj["ref"]) is { } reference)
                return ResolveRef(reference, pool, unavailable, problems);
            if (obj.Count == 1 && obj.ContainsKey("const"))
                return new Resolved(true, obj["const"]?.DeepClone());

            var nested = new JsonObject();
            foreach (var (key, child) in obj)
            {
                var value = Resolve(child, pool, unavailable, problems);
                if (value.Present)
                    nested[key] = value.Node;
            }

            return new Resolved(true, nested);
        }

        // Not a tag (manifests always tag); pass literals through.
        return new Resolved(true, mapping?.DeepClone());
    }

    private static Resolved ResolveRef(
        string reference,
        DevPool pool,
        List<(string Root, PoolEntry? Entry)> unavailable,
        List<string> problems)
    {
        var segments = reference.Split('.');
        var root = segments[0];
        JsonNode? current;

        if (root == "input")
        {
            current = pool.Input;
            if (segments.Length > 1 && (pool.Input is null || !pool.Input.ContainsKey(segments[1])))
            {
                problems.Add($"pipeline input field '{segments[1]}' is not set; pass --input {segments[1]}=…");
                return Resolved.Absent;
            }
        }
        else if (root == "failure")
        {
            problems.Add("failure-handler steps take a FailureContext; pass it with --input-file");
            return Resolved.Absent;
        }
        else
        {
            pool.Steps.TryGetValue(root, out var entry);
            if (entry is not { IsSucceeded: true })
            {
                unavailable.Add((root, entry));
                return Resolved.Absent;
            }

            current = entry.Output;
        }

        for (var i = 1; i < segments.Length; i++)
        {
            if (current is not JsonObject o || !o.TryGetPropertyValue(segments[i], out current))
                return Resolved.Absent; // an absent optional field: leave it out
        }

        return new Resolved(true, current?.DeepClone());
    }
}
