using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Versions;

namespace Olve.Pipelines.Pipelines.Triggers;

/// <summary>
/// Validates run input against the pipeline input model of the version a new run binds to (the active
/// version, or the drain target while switching — spec §6.2), and starts runs with it. Manual input is
/// the model's <c>input</c> view (defaults filled in), then checked against the strict <c>output</c> view;
/// a trigger mapper's result is already the output view and is re-validated against it. The stored input
/// is the output view, so <c>when</c> sees defaulted fields. Transient.
/// </summary>
public class RunInputService(
    PipelineActivationService activations,
    PipelineVersionService versions,
    RunPlanCache plans,
    RunStartService starts)
{
    /// <summary>The version a run started now would bind to.</summary>
    public bool TryGetTargetVersion(Id<Pipeline> pipelineId, [NotNullWhen(true)] out PipelineVersion? version)
    {
        version = null;
        if (!activations.TryGet(pipelineId, out var activation))
            return false;
        var id = activation switch
        {
            PipelineActivation.Active a => a.VersionId,
            PipelineActivation.Draining d => d.ToVersionId,
            _ => throw new InvalidOperationException($"Unknown activation state '{activation.GetType().Name}'."),
        };
        return versions.TryGet(id, out version);
    }

    public Result<(PipelineVersion Version, Manifest Manifest)> GetTarget(Id<Pipeline> pipelineId)
    {
        if (!TryGetTargetVersion(pipelineId, out var version))
            return new ResultProblem("Pipeline '{0}' has no active version yet (reconcile its binding first).", pipelineId);
        if (plans.Get(version.Id).TryPickProblems(out var problems, out var plan))
            return Result.Failure<(PipelineVersion, Manifest)>(problems);
        return (version, plan.Manifest);
    }

    /// <summary>Validates manual input (input view + defaults, then output view); returns the stored JSON.</summary>
    public Result<string> PrepareManual(Manifest manifest, JsonNode? input)
    {
        if (!manifest.Models.TryGetValue(manifest.Input, out var model))
            return new ResultProblem("The input model '{0}' is missing from the manifest.", manifest.Input);

        if (JsonSchemaValidator.Validate(model.Input ?? model.Output, input).TryPickProblems(out var problems, out var filled))
            return Result.Failure<string>(problems);
        return PrepareMapped(manifest, filled);
    }

    /// <summary>Re-validates a trigger mapper's result (already the output view); returns the stored JSON.</summary>
    public Result<string> PrepareMapped(Manifest manifest, JsonNode? input)
    {
        if (!manifest.Models.TryGetValue(manifest.Input, out var model))
            return new ResultProblem("The input model '{0}' is missing from the manifest.", manifest.Input);
        if (JsonSchemaValidator.Validate(model.Output, input).TryPickProblems(out var problems, out var value))
            return Result.Failure<string>(problems);
        if (value is not JsonObject)
            return new ResultProblem("Run input must be a JSON object.");
        return value.ToJsonString();
    }

    /// <summary>Validates and starts an explicit (manual) run.</summary>
    public Result<RunStartOutcome> StartManual(Id<Pipeline> pipelineId, JsonNode? input)
    {
        if (GetTarget(pipelineId).TryPickProblems(out var problems, out var target))
            return Result.Failure<RunStartOutcome>(problems);
        if (PrepareManual(target.Manifest, input).TryPickProblems(out var inputProblems, out var json))
            return Result.Failure<RunStartOutcome>(inputProblems);
        return starts.Start(pipelineId, json, new RunTrigger(RunTriggerKind.Manual));
    }
}
