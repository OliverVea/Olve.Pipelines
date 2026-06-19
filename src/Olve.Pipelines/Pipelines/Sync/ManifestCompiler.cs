using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>
/// Compiles a <c>.pipelines/</c> file tree into a validated <see cref="PipelineManifest"/>.
/// The tree is a map of paths (relative to the config directory) to file contents — e.g.
/// <c>config.yaml</c>, <c>steps/build.yaml</c>, <c>scripts/build.sh</c>.
///
/// Steps may be extracted to their own file and pulled in with <c>$ref: steps/&lt;name&gt;.yaml</c>;
/// a step's script may be kept out-of-line with <c>scriptFile: scripts/&lt;name&gt;.sh</c>.
/// References are resolved on the JSON DOM before deserialization, then the result is validated.
/// </summary>
public partial class ManifestCompiler(FailureHandlers.FailureHandlerLibrary? library = null)
{
    public const string ConfigFileName = "config.yaml";

    private readonly FailureHandlers.FailureHandlerLibrary _library = library ?? new FailureHandlers.FailureHandlerLibrary();

    public Result<PipelineManifest> Compile(IReadOnlyDictionary<string, string> files)
    {
        if (!files.TryGetValue(ConfigFileName, out var configYaml))
            return new ResultProblem($"Config file '{ConfigFileName}' not found in the config directory.");

        JsonNode? root;
        try
        {
            root = YamlJsonBridge.Parse(configYaml);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            return new ResultProblem($"Failed to parse '{ConfigFileName}': {ex.Message}");
        }

        if (root is not JsonObject manifestObj)
            return new ResultProblem($"'{ConfigFileName}' must be a YAML mapping.");

        if (ResolveStepList(manifestObj, "productionSteps", files).TryPickProblems(out var prodProblems))
            return prodProblems;
        if (ResolveStepList(manifestObj, "processingSteps", files).TryPickProblems(out var procProblems))
            return procProblems;

        PipelineManifest? manifest;
        try
        {
            manifest = manifestObj.Deserialize(ManifestJsonContext.Default.PipelineManifest);
        }
        catch (JsonException ex)
        {
            return new ResultProblem($"Invalid config structure: {ex.Message}");
        }

        if (manifest is null)
            return new ResultProblem($"'{ConfigFileName}' deserialized to nothing.");
        if (string.IsNullOrWhiteSpace(manifest.Name))
            return new ResultProblem("Config 'name' is required.");

        if (Validate(manifest, _library.Names).TryPickProblems(out var validationProblems))
            return validationProblems;

        return manifest;
    }

    /// <summary>Replaces each step element with its resolved object ($ref + scriptFile inlined).</summary>
    private static Result ResolveStepList(JsonObject manifest, string listKey, IReadOnlyDictionary<string, string> files)
    {
        if (manifest[listKey] is not JsonArray array)
            return Result.Success();

        // Detach children up front so resolved nodes can be re-parented without "node already has a parent".
        var children = new List<JsonNode?>(array.Count);
        while (array.Count > 0)
        {
            children.Add(array[0]);
            array.RemoveAt(0);
        }

        foreach (var child in children)
        {
            if (ResolveStep(child, files).TryPickProblems(out var problems, out var resolved))
                return problems;

            array.Add((JsonNode?)resolved);
        }

        return Result.Success();
    }

    private static Result<JsonObject> ResolveStep(JsonNode? child, IReadOnlyDictionary<string, string> files)
    {
        if (child is not JsonObject step)
            return new ResultProblem("Each step must be a mapping.");

        if (step["$ref"] is JsonValue refValue && refValue.TryGetValue(out string? refPath))
        {
            if (step.Count > 1)
                return new ResultProblem($"Step '$ref: {refPath}' must not set any other fields.");
            if (!files.TryGetValue(refPath!, out var refYaml))
                return new ResultProblem($"Referenced step file '{refPath}' not found.");

            JsonNode? parsed;
            try
            {
                parsed = YamlJsonBridge.Parse(refYaml);
            }
            catch (YamlDotNet.Core.YamlException ex)
            {
                return new ResultProblem($"Failed to parse '{refPath}': {ex.Message}");
            }

            if (parsed is not JsonObject refObject)
                return new ResultProblem($"Referenced step file '{refPath}' must be a YAML mapping.");

            step = refObject;
        }

        if (step["configuration"] is JsonObject configuration
            && ResolveScriptFile(configuration, files).TryPickProblems(out var scriptProblems))
            return scriptProblems;

        return step;
    }

    private static Result ResolveScriptFile(JsonObject configuration, IReadOnlyDictionary<string, string> files)
    {
        if (configuration["scriptFile"] is not JsonValue scriptFileValue
            || !scriptFileValue.TryGetValue(out string? scriptPath))
            return Result.Success();

        if (configuration["script"] is not null)
            return new ResultProblem($"Step configuration sets both 'script' and 'scriptFile' ('{scriptPath}'); use one.");
        if (!files.TryGetValue(scriptPath!, out var scriptContent))
            return new ResultProblem($"Script file '{scriptPath}' not found.");

        configuration.Remove("scriptFile");
        configuration["script"] = scriptContent;
        return Result.Success();
    }

    private static Result Validate(PipelineManifest manifest, IReadOnlyCollection<string> validHandlerNames)
    {
        var problems = new List<ResultProblem>();

        if (PipelineDocumentVersion.EnsureCompatible(manifest.ApiVersion).TryPickProblems(out var versionProblems))
            problems.AddRange(versionProblems);

        var production = manifest.ProductionSteps ?? [];
        var processing = manifest.ProcessingSteps ?? [];
        var triggers = manifest.Triggers ?? [];
        var failureHandlers = manifest.FailureHandlers ?? [];

        AddDuplicateNameProblems(problems, "production step", production.Select(s => s.Name));
        AddDuplicateNameProblems(problems, "processing step", processing.Select(s => s.Name));

        var processingNames = new HashSet<string>(processing.Select(s => s.Name), StringComparer.Ordinal);
        foreach (var trigger in triggers)
        {
            if (trigger.Target is ProcessingTargetDocument target && !processingNames.Contains(target.ProcessingStepName))
                problems.Add(new ResultProblem(
                    $"Trigger '{trigger.Name}' references unknown processing step '{target.ProcessingStepName}'."));
        }

        var stepNames = new HashSet<string>(StringComparer.Ordinal);
        stepNames.UnionWith(production.Select(s => s.Name));
        stepNames.UnionWith(processing.Select(s => s.Name));
        var handlerNames = new HashSet<string>(validHandlerNames, StringComparer.Ordinal);
        foreach (var handler in failureHandlers)
        {
            if (!handlerNames.Contains(handler.Handler))
                problems.Add(new ResultProblem(
                    $"Failure handler '{handler.Handler}' is not a known handler."));

            foreach (var step in handler.Steps ?? [])
            {
                if (!stepNames.Contains(step))
                    problems.Add(new ResultProblem(
                        $"Failure handler '{handler.Handler}' references unknown step '{step}'."));
            }
        }

        var declared = new HashSet<string>(
            (manifest.Secrets ?? []).Select(s => s.Name), StringComparer.Ordinal);
        foreach (var name in ReferencedSecretNames(production, processing, triggers, failureHandlers))
        {
            if (!declared.Contains(name))
                problems.Add(new ResultProblem(
                    $"Secret '$SECRET:{name}' is referenced but not declared in 'secrets:'."));
        }

        return problems.Count > 0 ? new ResultProblemCollection(problems) : Result.Success();
    }

    private static void AddDuplicateNameProblems(List<ResultProblem> problems, string label, IEnumerable<string> names)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (!seen.Add(name))
                problems.Add(new ResultProblem($"Duplicate {label} name '{name}'."));
        }
    }

    private static IEnumerable<string> ReferencedSecretNames(
        IReadOnlyList<ProductionStepDocument> production,
        IReadOnlyList<ProcessingStepDocument> processing,
        IReadOnlyList<TriggerDocument> triggers,
        IReadOnlyList<FailureHandlerDocument> failureHandlers)
    {
        var values = new List<string>();

        foreach (var step in production)
            CollectConfigValues(step.Configuration, values);
        foreach (var step in processing)
            CollectConfigValues(step.Configuration, values);
        foreach (var trigger in triggers)
        {
            if (trigger.Target is PollTargetDocument { Headers: { } headers })
                values.AddRange(headers.Values);
        }
        foreach (var handler in failureHandlers)
        {
            if (handler.Env is { } env)
                values.AddRange(env.Values);
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            foreach (Match match in SecretPattern().Matches(value))
                names.Add(match.Groups[1].Value);
        }

        return names;
    }

    private static void CollectConfigValues(StepConfigurationDocument? configuration, List<string> values)
    {
        if (configuration?.EnvironmentVariables is { } env)
            values.AddRange(env.Values);
    }

    [GeneratedRegex(@"\$SECRET:(\w+)")]
    private static partial Regex SecretPattern();
}
