using System.Text;
using System.Text.Json;
using Olve.Pipelines.Pipelines.Manifests;

namespace Olve.Pipelines.Pipelines.Scheduling;

/// <summary>What a ref root (a step name) currently provides in a run's pool.</summary>
public abstract record PoolEntry
{
    /// <summary>The step's latest successful attempt; <see cref="Output"/> is null for output-less steps.</summary>
    public sealed record Produced(JsonElement? Output) : PoolEntry;

    /// <summary>The step's latest attempt was skipped (by the scheduler or its handler).</summary>
    public sealed record Skipped(string Reason) : PoolEntry;

    /// <summary>The step has no successful attempt (and was not skipped).</summary>
    public sealed record Missing : PoolEntry;
}

/// <summary>A run's pool: the pipeline input plus each step's latest successful output (spec §5.2).</summary>
public interface IRunPool
{
    JsonElement Input { get; }
    PoolEntry Get(string stepName);
}

/// <summary>The outcome of building a step's <c>input.json</c>.</summary>
public abstract record AttemptInputResult
{
    /// <param name="Json">Compact JSON object with exactly the step's resolvable input keys.</param>
    /// <param name="Hash"><c>sha256:&lt;hex&gt;</c> of the canonical form (<see cref="CanonicalJson"/>).</param>
    public sealed record Built(string Json, string Hash) : AttemptInputResult;

    /// <summary>A required input needs an output that was not produced; the step is skipped with this (chained) reason.</summary>
    public sealed record Unavailable(string Reason) : AttemptInputResult;
}

/// <summary>
/// Builds a step's <c>input.json</c> from its tagged mappings over the run pool (spec §3.6/§4):
/// <c>{ref:"input.sha"}</c> walks the pipeline input, <c>{ref:"build"}</c> takes a step's whole output,
/// <c>{ref:"build.image"}</c> a field of it, <c>{const:…}</c> a literal, and nested objects compose these.
/// A ref whose path does not resolve is left out. A ref to a skipped (or never-produced) step makes the
/// enclosing input key unavailable: a required key skips the step with a chained reason, an
/// <c>optional(Model)</c> key is left out (the harness sees <c>undefined</c>, never <c>null</c>).
/// </summary>
public static class AttemptInputBuilder
{
    public static AttemptInputResult Build(StepDescriptor step, IRunPool pool)
    {
        var fields = new List<(string Key, Value Value)>();
        foreach (var (key, mapping) in step.Input)
        {
            switch (Evaluate(mapping, pool))
            {
                case Value value:
                    fields.Add((key, value));
                    break;
                case Absent:
                    break;
                case Unavailable when IsOptional(step, key):
                    break;
                case Unavailable u:
                    return new AttemptInputResult.Unavailable($"input '{key}' {u.Reason}");
            }
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, new ObjectValue(fields));
        }

        var json = Encoding.UTF8.GetString(buffer.ToArray());
        using var document = JsonDocument.Parse(buffer.ToArray());
        return new AttemptInputResult.Built(json, CanonicalJson.Hash(document.RootElement));
    }

    /// <summary>
    /// The pipeline-input fields a step's mappings read (<c>{ref:"input.x…"}</c> → <c>x</c>); a whole
    /// <c>{ref:"input"}</c> yields <see cref="WholeInput"/>.
    /// </summary>
    public static IEnumerable<string> ConsumedInputFields(StepDescriptor step)
        => step.Input.Values.SelectMany(InputRefs);

    /// <summary>Marker in <see cref="ConsumedInputFields"/> for a whole-input ref.</summary>
    public const string WholeInput = "*";

    private static IEnumerable<string> InputRefs(Mapping mapping) => mapping switch
    {
        RefMapping r when r.Root == ManifestRoots.Input => [r.Path.Count == 0 ? WholeInput : r.Path[0]],
        ObjectMapping o => o.Fields.Values.SelectMany(InputRefs),
        _ => [],
    };

    private static bool IsOptional(StepDescriptor step, string key)
        => step.InputTypes.TryGetValue(key, out var type) && type is ModelInputType { Optional: true };

    private abstract record Evaluated;
    private sealed record Absent : Evaluated;
    private sealed record Unavailable(string Reason) : Evaluated;
    private abstract record Value : Evaluated;
    private sealed record ElementValue(JsonElement? Element) : Value;
    private sealed record ObjectValue(IReadOnlyList<(string Key, Value Value)> Fields) : Value;

    private static Evaluated Evaluate(Mapping mapping, IRunPool pool)
    {
        switch (mapping)
        {
            case ConstMapping c:
                return new ElementValue(c.Value);
            case RefMapping r:
                return EvaluateRef(r, pool);
            case ObjectMapping o:
                var fields = new List<(string, Value)>();
                foreach (var (key, child) in o.Fields)
                {
                    switch (Evaluate(child, pool))
                    {
                        case Value v: fields.Add((key, v)); break;
                        case Unavailable u: return u;
                    }
                }
                return new ObjectValue(fields);
            default:
                return new Absent();
        }
    }

    private static Evaluated EvaluateRef(RefMapping r, IRunPool pool)
    {
        if (r.Root == ManifestRoots.Input)
            return Walk(pool.Input, r.Path);
        if (r.Root == ManifestRoots.Failure)
            return new Absent(); // failure context is built for failure handlers (B9), never in stages

        return pool.Get(r.Root) switch
        {
            PoolEntry.Produced { Output: { } output } => Walk(output, r.Path),
            PoolEntry.Produced => r.Path.Count == 0 ? new ElementValue(null) : new Absent(),
            PoolEntry.Skipped s => new Unavailable($"needs {r.Root} (skipped: {s.Reason})"),
            _ => new Unavailable($"needs {r.Root}, which has no successful attempt"),
        };
    }

    private static Evaluated Walk(JsonElement root, IReadOnlyList<string> path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return new Absent();
        }

        return new ElementValue(current);
    }

    private static void Write(Utf8JsonWriter writer, Value value)
    {
        switch (value)
        {
            case ElementValue { Element: { } element }:
                element.WriteTo(writer);
                break;
            case ElementValue:
                writer.WriteNullValue();
                break;
            case ObjectValue o:
                writer.WriteStartObject();
                foreach (var (key, child) in o.Fields)
                {
                    writer.WritePropertyName(key);
                    Write(writer, child);
                }
                writer.WriteEndObject();
                break;
        }
    }
}
