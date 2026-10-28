using Microsoft.Extensions.Hosting;
using Olve.Pipelines.Pipelines.Failures;

namespace Olve.Pipelines.Kubernetes.Harness;

/// <summary>
/// Runs a failure handler as an untracked harness Job (spec §3.6): store its <c>input.json</c> (the
/// FailureContext), submit <c>run &lt;handler&gt;</c> on the failed run's pinned binary, wait for a verdict,
/// persist the log to <see cref="FailureHandlerDispatch.LogKey"/> and delete the URL Secret. Best-effort:
/// nothing is retried and no outcome feeds back into the run. The Job name is deterministic, so a duplicate
/// dispatch reattaches. On shutdown the wait is abandoned (the Job finishes on its own; retention sweeps the
/// URL Secret). Each launch runs in the background and never throws.
/// </summary>
public sealed class HarnessFailureHandlerLauncher(
    IServiceScopeFactory scopes,
    IHostApplicationLifetime lifetime,
    ILogger<HarnessFailureHandlerLauncher> logger) : IFailureHandlerLauncher
{
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    public void Launch(FailureHandlerDispatch dispatch)
        => _ = Task.Run(() => RunAsync(dispatch, lifetime.ApplicationStopping));

    internal async Task RunAsync(FailureHandlerDispatch dispatch, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var services = scope.ServiceProvider;
        var storage = services.GetRequiredService<IHarnessStorage>();
        var factory = services.GetRequiredService<HarnessJobFactory>();
        var control = services.GetRequiredService<HarnessJobControl>();
        var options = services.GetRequiredService<HarnessOptions>();
        var submitted = false;

        try
        {
            await storage.PutTextAsync(dispatch.InputKey, dispatch.InputJson, "application/json", ct);

            var spec = await factory.CreateFailureHandlerJobAsync(dispatch, ct);
            if (spec.TryPickProblems(out var problems, out var built))
            {
                logger.LogProblems(LogLevel.Warning, problems, "Cannot build the Job of failure handler '{HandlerName}' for run #{RunNumber} of pipeline '{PipelineName}' ('{PipelineId}')",
                    dispatch.Handler.Name, dispatch.Run.Number, dispatch.Pipeline.Name, dispatch.Pipeline.Id);
                return;
            }

            submitted = true;
            var created = await control.SubmitAsync(built, ct);
            logger.LogInformation("{Action} K8s Job '{JobName}' for failure handler '{HandlerName}' (run #{RunNumber}, version {VersionNumber}, image {Image}, pipeline '{PipelineName}' ('{PipelineId}'))",
                created ? "Created" : "Found existing", dispatch.JobName, dispatch.Handler.Name, dispatch.Run.Number,
                dispatch.Version.Number, dispatch.Handler.Image, dispatch.Pipeline.Name, dispatch.Pipeline.Id);

            var timeout = TimeSpan.FromSeconds(options.DeadlineSeconds(dispatch.Handler.TimeoutSeconds) + options.StartupGraceSeconds);
            var wait = await control.WaitAsync(dispatch.JobName, PollInterval, shouldAbort: null, timeout, ct);
            await FinishAsync(dispatch, wait, control, storage, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogInformation("Shutting down; no longer watching failure handler Job '{JobName}'", dispatch.JobName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failure handler '{HandlerName}' for run #{RunNumber} of pipeline '{PipelineName}' ('{PipelineId}') could not be run",
                dispatch.Handler.Name, dispatch.Run.Number, dispatch.Pipeline.Name, dispatch.Pipeline.Id);
        }
        finally
        {
            if (submitted && !ct.IsCancellationRequested)
                await control.DeleteSecretAsync(dispatch.UrlSecretName);
        }
    }

    private async Task FinishAsync(
        FailureHandlerDispatch dispatch, HarnessWait wait, HarnessJobControl control, IHarnessStorage storage, CancellationToken ct)
    {
        string outcome;
        string log;
        var ok = false;

        switch (wait)
        {
            case HarnessWait.Finished finished:
                {
                    var logs = await control.GetLogsAsync(dispatch.JobName, HarnessJobBuilder.StepContainerName, hasInit: true, ct);
                    log = logs.Combined(HarnessJobBuilder.StepContainerName);
                    var block = HarnessSentinels.TryExtractResult(logs.Main);
                    HarnessEnvelope? envelope = null;
                    if (block is not null && !HarnessEnvelope.Parse(block).TryPickProblems(out _, out var parsed))
                        envelope = parsed;

                    (ok, outcome) = envelope switch
                    {
                        HarnessEnvelope.Succeeded when finished.State.Main is { ExitCode: 0 } => (true, "succeeded"),
                        HarnessEnvelope.Skipped s => (true, $"skipped: {s.Reason}"),
                        HarnessEnvelope.Failed f => (false, $"failed ({f.Kind}): {f.Message}"),
                        _ => (false, "failed: " + (HarnessExitCodes.Classify(finished.State)?.Message ?? "no result in the log")),
                    };
                    break;
                }
            case HarnessWait.TimedOut:
                await control.DeleteJobAsync(dispatch.JobName);
                log = "";
                outcome = "timed out; K8s Job deleted";
                break;
            default:
                log = "";
                outcome = $"K8s Job disappeared ({wait.GetType().Name})";
                break;
        }

        try
        {
            await storage.PutTextAsync(dispatch.LogKey, log + $"controller: failure handler {outcome}\n", "text/plain; charset=utf-8", ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Failed to persist the log of failure handler Job '{JobName}' to '{LogKey}'", dispatch.JobName, dispatch.LogKey);
        }

        if (ok)
            logger.LogInformation("Failure handler '{HandlerName}' for run #{RunNumber} of pipeline '{PipelineName}' ('{PipelineId}') {Outcome}; log at '{LogKey}'",
                dispatch.Handler.Name, dispatch.Run.Number, dispatch.Pipeline.Name, dispatch.Pipeline.Id, outcome, dispatch.LogKey);
        else
            logger.LogWarning("Failure handler '{HandlerName}' for run #{RunNumber} of pipeline '{PipelineName}' ('{PipelineId}') {Outcome}; log at '{LogKey}'",
                dispatch.Handler.Name, dispatch.Run.Number, dispatch.Pipeline.Name, dispatch.Pipeline.Id, outcome, dispatch.LogKey);
    }
}
