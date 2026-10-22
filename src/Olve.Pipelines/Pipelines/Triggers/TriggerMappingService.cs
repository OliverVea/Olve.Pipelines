using System.Text.Json.Nodes;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;

namespace Olve.Pipelines.Pipelines.Triggers;

/// <summary>
/// Automatic triggers (spec §3.6, §5.3): one event → a short <c>map-trigger &lt;name&gt;</c> Job on the
/// binary of the version a run would bind to → the mapped input (from the result block in the pod log) →
/// server-side validation → <see cref="RunStartService.Start"/> with the automatic trigger kind. De-dup
/// uses the default key (the canonical hash of the mapped input), so the same commit arriving by push and
/// by poll yields one run. A webhook mapper may ignore its event (returns null): nothing starts.
/// Async I/O boundary; transient.
/// </summary>
public class TriggerMappingService(
    RunInputService inputs,
    RunStartService starts,
    HarnessJobFactory factory,
    HarnessJobControl control,
    IHarnessStorage storage,
    HarnessOptions options,
    ILogger<TriggerMappingService> logger)
{
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    public static string EventKey(Id<Pipeline> pipelineId, Guid eventId)
        => $"triggers/{pipelineId.Value.Value:N}/{eventId:N}/event.json";

    /// <returns>The run start outcome, or null when the trigger's mapper ignored the event.</returns>
    public async Task<Result<RunStartOutcome?>> MapAndStartAsync(
        Id<Pipeline> pipelineId, string triggerName, RunTriggerKind kind, string eventJson, CancellationToken ct)
    {
        if (inputs.GetTarget(pipelineId).TryPickProblems(out var problems, out var target))
            return Result.Failure<RunStartOutcome?>(problems);
        if (target.Manifest.Triggers.All(t => t.Name != triggerName))
            return new ResultProblem("Version {0} of pipeline '{1}' declares no trigger '{2}'.", target.Version.Number, pipelineId, triggerName);

        var mapped = await MapAsync(pipelineId, target.Version.BinaryKey, triggerName, eventJson, ct);
        if (mapped.TryPickProblems(out var mapProblems, out var output))
            return Result.Failure<RunStartOutcome?>(mapProblems);
        if (output is null)
        {
            logger.LogInformation("Trigger '{TriggerName}' of pipeline '{PipelineId}' ({TriggerKind}) ignored its event on version {VersionNumber}",
                triggerName, pipelineId, kind, target.Version.Number);
            return (RunStartOutcome?)null;
        }

        if (inputs.PrepareMapped(target.Manifest, JsonNode.Parse(output)).TryPickProblems(out var inputProblems, out var input))
            return Result.Failure<RunStartOutcome?>(inputProblems.Prepend("Trigger '{0}' produced invalid input.", triggerName));

        var outcome = starts.Start(pipelineId, input, new RunTrigger(kind));
        if (outcome.TryPickProblems(out var startProblems, out var started))
            return Result.Failure<RunStartOutcome?>(startProblems);

        logger.LogInformation("Trigger '{TriggerName}' of pipeline '{PipelineId}' ({TriggerKind}) mapped on version {VersionNumber}: {Outcome} (run #{RunNumber})",
            triggerName, pipelineId, kind, target.Version.Number, started.GetType().Name, RunNumber(started));
        return started;
    }

    private static int? RunNumber(RunStartOutcome outcome) => outcome switch
    {
        RunStartOutcome.Started s => s.Run.Number,
        RunStartOutcome.Deduplicated d => d.Existing.Number,
        _ => null,
    };

    /// <summary>Runs the map-trigger Job; returns the envelope's output JSON, or null when the mapper ignored the event.</summary>
    private async Task<Result<string?>> MapAsync(Id<Pipeline> pipelineId, string binaryKey, string triggerName, string eventJson, CancellationToken ct)
    {
        var eventId = Guid.NewGuid();
        var eventKey = EventKey(pipelineId, eventId);
        var jobName = $"olve-map-{eventId:N}"[..41];

        await storage.PutTextAsync(eventKey, eventJson, "application/json", ct);
        var spec = await factory.CreateMapTriggerJobAsync(jobName, pipelineId, binaryKey, triggerName, eventKey, ct);
        if (spec.TryPickProblems(out var specProblems, out var built))
            return Result.Failure<string?>(specProblems);

        try
        {
            await control.SubmitAsync(built, ct);
            var wait = await control.WaitAsync(jobName, PollInterval, shouldAbort: null,
                TimeSpan.FromSeconds(options.MapTriggerTimeoutSeconds + options.StartupGraceSeconds), ct);
            if (wait is not HarnessWait.Finished finished)
            {
                await control.DeleteJobAsync(jobName);
                return new ResultProblem("The map-trigger Job '{0}' for trigger '{1}' did not finish ({2}).", jobName, triggerName, wait.GetType().Name);
            }

            var logs = await control.GetLogsAsync(jobName, HarnessJobBuilder.StepContainerName, hasInit: true, ct);
            var block = HarnessSentinels.TryExtractResult(logs.Main);
            HarnessEnvelope? envelope = null;
            if (block is not null && !HarnessEnvelope.Parse(block).TryPickProblems(out _, out var parsed))
                envelope = parsed;

            if (envelope is HarnessEnvelope.Succeeded s && finished.State.Main is { ExitCode: 0 })
                return s.OutputJson;
            if (envelope is HarnessEnvelope.Skipped && finished.State.Main is { ExitCode: 0 })
                return (string?)null;

            var detail = envelope is HarnessEnvelope.Failed f
                ? f.Message
                : HarnessExitCodes.Classify(finished.State)?.Message ?? "no result in the log";
            logger.LogWarning("Map-trigger Job '{JobName}' for trigger '{TriggerName}' of pipeline '{PipelineId}' failed: {Detail}",
                jobName, triggerName, pipelineId, detail);
            return new ResultProblem("Trigger '{0}' mapping failed: {1}", triggerName, detail);
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                await control.DeleteSecretAsync(built.UrlSecretName);
                await storage.DeleteAsync(eventKey, CancellationToken.None);
            }
        }
    }
}
