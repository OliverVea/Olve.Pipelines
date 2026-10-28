using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Olve.Pipelines.Pipelines.Configs;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Versions;

namespace Olve.Pipelines.Pipelines.Failures;

/// <summary>
/// One failure-handler run to launch: the handler step from the run's pinned manifest, its assembled
/// <c>input.json</c> and where its objects live. <see cref="JobName"/> is deterministic per (run, failure,
/// handler), so a duplicate dispatch reattaches instead of running twice.
/// </summary>
/// <param name="DispatchId">24 lowercase hex chars; names the Job, its URL Secret and its storage prefix.</param>
/// <param name="FailedSteps">The failed steps in the handler's scope (for logs).</param>
public sealed record FailureHandlerDispatch(
    string DispatchId,
    Pipeline Pipeline,
    Run Run,
    PipelineVersion Version,
    StepDescriptor Handler,
    string InputJson,
    IReadOnlyList<string> FailedSteps)
{
    public const string JobNamePrefix = "olve-fh-";

    public string JobName => JobNamePrefix + DispatchId;

    /// <summary>The Job's URL Secret (<c>&lt;job&gt;-urls</c>, like map-trigger and build Jobs).</summary>
    public string UrlSecretName => $"{JobName}-urls";

    public string InputKey => $"{StorageLayout.FailureHandlerPrefix(Run.PipelineId, Run.Number, Handler.Name, DispatchId)}/input.json";

    public string LogKey => $"{StorageLayout.FailureHandlerPrefix(Run.PipelineId, Run.Number, Handler.Name, DispatchId)}/log";
}

/// <summary>A handler that was in scope but could not be dispatched (logged, never fatal).</summary>
public sealed record FailureHandlerSkip(string Handler, string Reason);

/// <summary>
/// Decides which failure handlers of a failed run's pinned manifest run, and builds their input (spec §3.6
/// "Failure handlers"). A handler runs when its scope (null = any step) contains one of the run's failed
/// steps; its <c>failedSteps</c> are the failed steps in that scope. The context is projected onto the
/// pinned manifest's <c>FailureContext</c> schema (its top-level properties) and validated against it, so a
/// version built with an older SDK whose FailureContext differs is skipped with a reason instead of
/// launching a handler the harness would reject (exit 65). Pure.
/// </summary>
public static class FailureHandlerPlanner
{
    public static (IReadOnlyList<FailureHandlerDispatch> Dispatches, IReadOnlyList<FailureHandlerSkip> Skipped) Plan(
        Pipeline pipeline, Run run, PipelineVersion version, Manifest manifest, IReadOnlyList<Attempt> failedAttempts, string linkBase)
    {
        var dispatches = new List<FailureHandlerDispatch>();
        var skipped = new List<FailureHandlerSkip>();
        if (failedAttempts.Count == 0 || manifest.FailureHandlers.Count == 0)
            return (dispatches, skipped);

        for (var i = 0; i < manifest.FailureHandlers.Count; i++)
        {
            var fh = manifest.FailureHandlers[i];
            var inScope = fh.Steps is null
                ? failedAttempts
                : failedAttempts.Where(a => fh.Steps.Contains(a.StepName, StringComparer.Ordinal)).ToList();
            if (inScope.Count == 0)
                continue;

            var context = FailureContextBuilder.Build(pipeline, run, version, inScope, linkBase);
            var input = BuildInput(fh.Step, context, manifest);
            if (input.TryPickProblems(out var problems, out var inputJson))
            {
                skipped.Add(new FailureHandlerSkip(fh.Step.Name, string.Join("; ", problems.Select(p => p.ToDisplayString()))));
                continue;
            }

            dispatches.Add(new FailureHandlerDispatch(
                DispatchId(run, i, fh.Step.Name), pipeline, run, version, fh.Step, ConfigDelivery.WithConfig(inputJson, fh.Step, run.Config),
                inScope.Select(a => a.StepName).ToList()));
        }

        return (dispatches, skipped);
    }

    /// <summary>Hash of (run, the failure — its finish time, handler index + name): unique per failure episode
    /// (a continued run that fails again dispatches again), stable for the same one.</summary>
    public static string DispatchId(Run run, int handlerIndex, string handlerName)
    {
        var finished = (run.FinishedAt ?? run.CreatedAt).UtcTicks.ToString(CultureInfo.InvariantCulture);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{run.Id.Value.Value:N}|{finished}|{handlerIndex}|{handlerName}"));
        return Convert.ToHexStringLower(bytes)[..24];
    }

    /// <summary>The handler's <c>input.json</c>: each input key's <c>{ref: "failure[.path]"}</c> resolved against the
    /// (projected, validated) context; <c>{const}</c> copied.</summary>
    internal static Result<string> BuildInput(StepDescriptor handler, JsonObject context, Manifest manifest)
    {
        if (!manifest.Models.TryGetValue(ManifestRoots.FailureContextModel, out var schemas))
            return new ResultProblem("the manifest has no {0} model", ManifestRoots.FailureContextModel);

        var projected = Project(context, schemas.Output);
        var validated = JsonSchemaValidator.Validate(schemas.Output, projected, ManifestRoots.Failure);
        if (validated.TryPickProblems(out var problems, out var value))
            return Result.Failure<string>(problems.Prepend("the run's {0} does not match this version's {0} model", ManifestRoots.FailureContextModel));

        var input = new JsonObject();
        foreach (var (key, mapping) in handler.Input)
        {
            switch (mapping)
            {
                case RefMapping r when r.Root == ManifestRoots.Failure:
                    JsonNode? node = value;
                    foreach (var segment in r.Path)
                        node = node is JsonObject o && o.TryGetPropertyValue(segment, out var child) ? child : null;
                    if (node is null && r.Path.Count > 0)
                        return new ResultProblem("input '{0}': '{1}' is not in the context", key, r.Ref);
                    input[key] = node?.DeepClone();
                    break;
                case ConstMapping c:
                    input[key] = JsonNode.Parse(c.Value.GetRawText());
                    break;
                default:
                    return new ResultProblem("input '{0}' is not mapped from the failure context", key);
            }
        }

        return input.ToJsonString();
    }

    /// <summary>Keeps only the top-level properties the schema declares (an older FailureContext may lack newer fields).</summary>
    private static JsonObject Project(JsonObject context, JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
            return (JsonObject)context.DeepClone();

        var projected = new JsonObject();
        foreach (var (name, node) in context)
        {
            if (properties.TryGetProperty(name, out _))
                projected[name] = node?.DeepClone();
        }

        return projected;
    }
}
