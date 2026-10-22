using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Olve.Pipelines.Cli.Addressing;
using Olve.Pipelines.Cli.Api.Contracts;
using Olve.Pipelines.Cli.Commands.Dev;

namespace Olve.Pipelines.Cli.Commands.Runs;

/// <summary>A parsed address with its pipeline resolved to an id; <see cref="Label"/> is how the user named the pipeline.</summary>
public sealed record ResolvedAddress(Guid PipelineId, string Label, RunAddress Address)
{
    public string Id => PipelineId.ToString();
    public int Run => Address.Run;
    public string Step => Address.Step!;
    public int? Attempt => Address.Attempt;
}

/// <summary>Shared plumbing for the run/step/gate/version commands: address resolution, input assembly, formatting.</summary>
public static class RunCommandSupport
{
    public static async Task<Result<ResolvedAddress>> ResolveRunAsync(CommandContext ctx, string text, CancellationToken ct)
    {
        if (RunAddress.ParseRun(text).TryPickProblems(out var problems, out var address))
            return problems;
        return await ResolveAsync(ctx, address, ct);
    }

    public static async Task<Result<ResolvedAddress>> ResolveStepAsync(CommandContext ctx, string text, CancellationToken ct, bool allowPath = false)
    {
        if (RunAddress.ParseStep(text, allowPath).TryPickProblems(out var problems, out var address))
            return problems;
        return await ResolveAsync(ctx, address, ct);
    }

    private static async Task<Result<ResolvedAddress>> ResolveAsync(CommandContext ctx, RunAddress address, CancellationToken ct)
    {
        if ((await PipelineResolver.ResolveAsync(ctx.Api, address.Pipeline, ct)).TryPickProblems(out var problems, out var pipeline))
            return problems;
        return new ResolvedAddress(pipeline.Id, address.Pipeline, address);
    }

    public static async Task<Result<(Guid Id, string Label)>> ResolvePipelineAsync(CommandContext ctx, string text, CancellationToken ct)
    {
        if ((await PipelineResolver.ResolveAsync(ctx.Api, text, ct)).TryPickProblems(out var problems, out var pipeline))
            return problems;
        return (pipeline.Id, text);
    }

    /// <summary>
    /// Builds a pipeline-input object from <c>--input &lt;json|@file|@-&gt;</c> (the base) and repeated
    /// <c>--field k=v</c> (typed by <paramref name="schema"/>, the input model's <c>z.input</c> view, when known).
    /// No defaults are filled in — the service does that — so a continue sends exactly the changed fields.
    /// </summary>
    public static Result<JsonObject> BuildInput(string? inputOption, IReadOnlyList<string> fields, JsonObject? schema)
    {
        JsonObject input;
        if (inputOption is null)
        {
            input = new JsonObject();
        }
        else
        {
            if (ReadInputText(inputOption).TryPickProblems(out var readProblems, out var text))
                return readProblems;
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(text);
            }
            catch (JsonException ex)
            {
                return new ResultProblem("--input is not valid JSON: {0}", ex.Message);
            }

            if (node is not JsonObject obj)
                return new ResultProblem("--input must be a JSON object (the pipeline input).");
            input = obj;
        }

        var properties = schema?["properties"] as JsonObject;
        var problems = new List<ResultProblem>();
        foreach (var field in fields)
        {
            var eq = field.IndexOf('=');
            if (eq <= 0)
            {
                problems.Add(new ResultProblem("--field expects key=value, got '{0}'.", field));
                continue;
            }

            var key = field[..eq];
            if (properties is not null && !properties.ContainsKey(key))
            {
                problems.Add(new ResultProblem("Unknown pipeline input field '{0}' (known: {1}).",
                    key, string.Join(", ", properties.Select(p => p.Key))));
                continue;
            }

            if (PipelineInput.Coerce(key, field[(eq + 1)..], properties?[key] as JsonObject).TryPickProblems(out var coerce, out var value))
            {
                problems.AddRange(coerce);
                continue;
            }

            input[key] = value;
        }

        return problems.Count > 0 ? new ResultProblemCollection(problems) : input;
    }

    private static Result<string> ReadInputText(string option)
    {
        if (!option.StartsWith('@'))
            return option;
        var path = option[1..];
        try
        {
            return path == "-" ? Console.In.ReadToEnd() : File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ResultProblem("Could not read --input file '{0}': {1}", path, ex.Message);
        }
    }

    /// <summary>
    /// The input model schema of a pipeline version (<paramref name="version"/>; null = the version runs start on),
    /// used to type <c>--field</c> values. Best-effort: null (untyped) when it cannot be fetched.
    /// </summary>
    public static async Task<JsonObject?> InputSchemaAsync(CommandContext ctx, string pipelineId, int? version, CancellationToken ct)
    {
        if (version is null)
        {
            if ((await ctx.Api.ListPipelineVersions(pipelineId, ct)).TryPickProblems(out var listProblems, out var versions))
            {
                ctx.Log.Log($"input schema unavailable (fields sent untyped): {string.Join("; ", listProblems)}");
                return null;
            }

            version = versions.Activation?.CurrentVersion;
            if (version is null)
                return null;
        }

        if ((await ctx.Api.GetPipelineVersion(pipelineId, version.Value, ct)).TryPickProblems(out var problems, out var pv))
        {
            ctx.Log.Log($"input schema unavailable (fields sent untyped): {string.Join("; ", problems)}");
            return null;
        }

        return DevManifest.Parse(pv.Manifest).TryPickProblems(out _, out var manifest) ? null : manifest.InputSchema;
    }

    public static string Time(DateTimeOffset? value) =>
        value is { } v ? v.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "-";

    public static string Duration(DateTimeOffset? start, DateTimeOffset? end)
    {
        if (start is not { } s || end is not { } e || e < s)
            return "-";
        var d = e - s;
        return d.TotalHours >= 1 ? $"{(int)d.TotalHours}h{d.Minutes:00}m"
            : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}m{d.Seconds:00}s"
            : $"{d.TotalSeconds:0}s";
    }

    public static string Short(string? sha) => sha is { Length: > 7 } ? sha[..7] : sha ?? "-";

    public static string Json(JsonElement element) => element.ValueKind is JsonValueKind.Undefined ? "null" : element.GetRawText();

    public static string Indented(JsonElement element) =>
        element.ValueKind is JsonValueKind.Undefined
            ? "null"
            : JsonNode.Parse(element.GetRawText())?.ToJsonString(DevPool.IndentedJson) ?? "null";

    public static bool IsActive(Attempt attempt) => attempt.Status is ScheduledStatus or InProgressStatus;
}
