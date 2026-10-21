using System.Text.Json;
using System.Text.Json.Nodes;

namespace Olve.Pipelines.Cli.Commands.Dev;

/// <summary>One step of the manifest, as much of it as local development needs.</summary>
public sealed record DevStep(
    string Name,
    string Kind,
    string Image,
    string? Output,
    IReadOnlyList<string> Secrets,
    JsonObject Input,
    IReadOnlyDictionary<string, bool> OptionalInputs,
    bool IsFailureHandler);

/// <summary>A <c>when</c> block condition: the pipeline-input field must equal one of the values.</summary>
public sealed record WhenCondition(string Field, IReadOnlyList<JsonNode?> Values)
{
    public override string ToString() =>
        $"{Field} in [{string.Join(", ", Values.Select(v => v?.ToJsonString() ?? "null"))}]";
}

/// <summary>A manifest stage node: either parallel steps (with a barrier) or a nested <c>when</c> block.</summary>
public abstract record DevStageNode;

public sealed record DevStage(IReadOnlyList<DevStep> Steps) : DevStageNode;

public sealed record DevWhenBlock(IReadOnlyList<WhenCondition> Conditions, IReadOnlyList<DevStageNode> Nodes) : DevStageNode;

/// <summary>
/// The parts of the manifest (<c>sdk/manifest.schema.json</c>) that <c>pl dev</c> uses, parsed with
/// <see cref="JsonNode"/> (AOT-safe, no reflection). The raw text is kept for <c>--json</c>.
/// </summary>
public sealed class DevManifest
{
    public required string Text { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? InputModel { get; init; }

    /// <summary>The pipeline input model's <c>z.input</c> view (falls back to the output view).</summary>
    public JsonObject? InputSchema { get; init; }

    /// <summary>The pipeline input model's <c>z.output</c> view (what the pool holds).</summary>
    public JsonObject? InputOutputSchema { get; init; }

    public required IReadOnlyList<(string Name, string Description)> Secrets { get; init; }
    public required IReadOnlyList<JsonObject> Triggers { get; init; }
    public required IReadOnlyList<DevStageNode> Stages { get; init; }
    public required IReadOnlyList<(DevStep Step, IReadOnlyList<string>? Scope)> FailureHandlers { get; init; }

    /// <summary>Every step in pipeline order, then failure-handler steps.</summary>
    public IEnumerable<DevStep> AllSteps()
    {
        foreach (var step in StageSteps(Stages))
            yield return step;
        foreach (var (step, _) in FailureHandlers)
            yield return step;
    }

    public DevStep? FindStep(string name) => AllSteps().FirstOrDefault(s => s.Name == name);

    private static IEnumerable<DevStep> StageSteps(IEnumerable<DevStageNode> nodes)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case DevStage stage:
                    foreach (var step in stage.Steps)
                        yield return step;
                    break;
                case DevWhenBlock block:
                    foreach (var step in StageSteps(block.Nodes))
                        yield return step;
                    break;
            }
        }
    }

    public static Result<DevManifest> Parse(string text)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            return new ResultProblem("The manifest printed by describe is not JSON: {0}", ex.Message);
        }

        if (root is not JsonObject obj)
            return new ResultProblem("The manifest printed by describe is not a JSON object.");

        var inputModel = Str(obj["input"]);
        JsonObject? inputSchema = null, outputSchema = null;
        if (inputModel is not null && obj["models"]?[inputModel] is JsonObject model)
        {
            outputSchema = model["output"] as JsonObject;
            inputSchema = model["input"] as JsonObject ?? outputSchema;
        }

        var secrets = (obj["secrets"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(s => (Str(s["name"]) ?? "", Str(s["description"]) ?? ""))
            .ToList();

        var handlers = (obj["failureHandlers"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(h => h["step"] is JsonObject)
            .Select(h => (ParseStep((JsonObject)h["step"]!, isFailureHandler: true),
                h["steps"] is JsonArray scope ? (IReadOnlyList<string>?)scope.Select(Str).OfType<string>().ToList() : null))
            .ToList();

        return new DevManifest
        {
            Text = text,
            Name = Str(obj["name"]) ?? "pipeline",
            Description = Str(obj["description"]),
            InputModel = inputModel,
            InputSchema = inputSchema,
            InputOutputSchema = outputSchema,
            Secrets = secrets,
            Triggers = (obj["triggers"] as JsonArray ?? []).OfType<JsonObject>().ToList(),
            Stages = ParseNodes(obj["stages"] as JsonArray),
            FailureHandlers = handlers,
        };
    }

    private static List<DevStageNode> ParseNodes(JsonArray? nodes)
    {
        var result = new List<DevStageNode>();
        foreach (var node in (nodes ?? []).OfType<JsonObject>())
        {
            if (node["steps"] is JsonArray steps)
            {
                result.Add(new DevStage(steps.OfType<JsonObject>().Select(s => ParseStep(s, false)).ToList()));
            }
            else if (node["when"] is JsonObject when)
            {
                var conditions = when
                    .Select(kv => new WhenCondition(
                        kv.Key,
                        kv.Value is JsonArray values ? values.Select(v => v?.DeepClone()).ToList() : [kv.Value?.DeepClone()]))
                    .ToList();
                result.Add(new DevWhenBlock(conditions, ParseNodes(node["stages"] as JsonArray)));
            }
        }

        return result;
    }

    private static DevStep ParseStep(JsonObject step, bool isFailureHandler)
    {
        var optional = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (step["inputTypes"] is JsonObject types)
        {
            foreach (var (key, type) in types)
                optional[key] = type?["optional"] is JsonValue v && v.TryGetValue<bool>(out var o) && o;
        }

        return new DevStep(
            Name: Str(step["name"]) ?? "",
            Kind: Str(step["kind"]) ?? "custom",
            Image: Str(step["image"]) ?? "",
            Output: Str(step["output"]),
            Secrets: (step["secrets"] as JsonArray ?? []).Select(Str).OfType<string>().ToList(),
            Input: step["input"] is JsonObject input ? (JsonObject)input.DeepClone() : [],
            OptionalInputs: optional,
            IsFailureHandler: isFailureHandler);
    }

    internal static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
