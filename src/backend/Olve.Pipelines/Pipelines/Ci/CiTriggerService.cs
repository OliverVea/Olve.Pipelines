using System.Collections.Concurrent;
using Olve.Pipelines.Pipelines.Runs;

namespace Olve.Pipelines.Pipelines.Ci;

/// <summary>
/// Starts a pull request's CI run once its head has been still for <see cref="Debounce"/>: a burst of pushes makes
/// one run, for the last of them. A head gets one automatic run; a pull request closed, merged or retargeted away
/// from the binding's branch has its running run cancelled. Singleton: holds the pending starts.
/// </summary>
public class CiTriggerService(IServiceProvider sp, CiRunService runs, TimeProvider time, ILogger<CiTriggerService> logger) : BackgroundService
{
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<(Id<Pipeline> Pipeline, int Pull), (string Head, DateTimeOffset Due)> _pending = new();

    /// <summary>A tracked pull request was added or changed.</summary>
    public void Observe(PullRequest? before, PullRequest after)
    {
        var key = (after.PipelineId, after.Number);
        if (after.State != PullRequestState.Open)
        {
            _pending.TryRemove(key, out _);
            Cancel(after.PipelineId, after.Number, after.State == PullRequestState.Merged ? CiCancelReason.Merged : CiCancelReason.Closed);
            return;
        }

        var reopened = before is null || before.State != PullRequestState.Open;
        if (!reopened && before!.HeadSha == after.HeadSha)
            return;
        if (runs.ListByPull(after.PipelineId, after.Number).Any(r => r.HeadSha == after.HeadSha && r.Status is not RunStatus.Superseded and not RunStatus.Cancelled))
            return;
        _pending[key] = (after.HeadSha, time.GetUtcNow() + Debounce);
    }

    /// <summary>A pull request is no longer tracked: it left the binding's branch.</summary>
    public void Forget(PullRequest pull)
    {
        _pending.TryRemove((pull.PipelineId, pull.Number), out _);
        Cancel(pull.PipelineId, pull.Number, CiCancelReason.Closed);
    }

    /// <summary>The pending starts whose head has been still long enough, removed from the pending set.</summary>
    internal IReadOnlyList<(Id<Pipeline> Pipeline, int Pull, string Head)> TakeDue()
    {
        var now = time.GetUtcNow();
        var due = new List<(Id<Pipeline>, int, string)>();
        foreach (var (key, entry) in _pending)
        {
            if (entry.Due <= now && _pending.TryRemove(new KeyValuePair<(Id<Pipeline>, int), (string, DateTimeOffset)>(key, entry)))
                due.Add((key.Pipeline, key.Pull, entry.Head));
        }

        return due;
    }

    internal bool IsPending(Id<Pipeline> pipelineId, int pull) => _pending.ContainsKey((pipelineId, pull));

    /// <summary>Starts every due run, one after another.</summary>
    internal async Task StartDueAsync(CancellationToken ct)
    {
        foreach (var (pipelineId, pull, head) in TakeDue())
        {
            try
            {
                var started = await sp.GetRequiredService<CiRunStartService>().StartAsync(pipelineId, pull, head, ct);
                if (started.TryPickProblems(out var problems))
                    logger.LogProblems(LogLevel.Warning, problems, "Could not start the CI run of pull request #{Pull} in pipeline '{PipelineId}'", pull, pipelineId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not start the CI run of pull request #{Pull} in pipeline '{PipelineId}'", pull, pipelineId);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var swept = time.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            await StartDueAsync(stoppingToken);
            // A safety net for a run whose event handler threw: advancing is idempotent.
            if (time.GetUtcNow() - swept >= TimeSpan.FromMinutes(1))
            {
                swept = time.GetUtcNow();
                sp.GetRequiredService<CiScheduler>().AdvanceRunning();
            }

            await Task.Delay(Tick, time, stoppingToken);
        }
    }

    private void Cancel(Id<Pipeline> pipelineId, int pull, CiCancelReason reason) =>
        sp.GetRequiredService<CiCancellationService>().CancelPull(pipelineId, pull, reason);
}
