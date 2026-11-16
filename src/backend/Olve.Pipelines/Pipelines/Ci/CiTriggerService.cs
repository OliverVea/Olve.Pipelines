using System.Collections.Concurrent;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Versions;

namespace Olve.Pipelines.Pipelines.Ci;

/// <summary>
/// Starts a pull request's CI run once its head has been still for <see cref="Debounce"/>: a burst of pushes makes
/// one run, for the last of them. A head gets one automatic run; a pull request closed, merged or retargeted away
/// from the binding's branch has its running run cancelled. Meanwhile a new head gets <c>olve/ci</c> "Waiting to
/// start" (<see cref="AnnounceAsync"/>). Singleton: holds the pending starts.
/// </summary>
public class CiTriggerService(IServiceProvider sp, CiRunService runs, TimeProvider time, ILogger<CiTriggerService> logger) : BackgroundService
{
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<(Id<Pipeline> Pipeline, int Pull), (string Head, DateTimeOffset Due)> _pending = new();
    private readonly ConcurrentDictionary<(Id<Pipeline> Pipeline, int Pull), string> _toAnnounce = new();

    /// <summary>A tracked pull request was added or changed.</summary>
    public void Observe(PullRequest? before, PullRequest after)
    {
        var key = (after.PipelineId, after.Number);
        if (after.State != PullRequestState.Open)
        {
            _pending.TryRemove(key, out _);
            _toAnnounce.TryRemove(key, out _);
            Cancel(after.PipelineId, after.Number, after.State == PullRequestState.Merged ? CiCancelReason.Merged : CiCancelReason.Closed);
            return;
        }

        var reopened = before is null || before.State != PullRequestState.Open;
        if (!reopened && before!.HeadSha == after.HeadSha)
            return;
        if (runs.ListByPull(after.PipelineId, after.Number).Any(r => r.HeadSha == after.HeadSha && r.Status is not RunStatus.Superseded and not RunStatus.Cancelled))
            return;
        _pending[key] = (after.HeadSha, time.GetUtcNow() + Debounce);
        _toAnnounce[key] = after.HeadSha;
    }

    /// <summary>
    /// At startup: the pending starts were in memory, so each open pull request whose head has no run is observed
    /// again, as if just pushed. Its run starts after the debounce, and it gets "Waiting to start" again.
    /// </summary>
    public void Resume(IEnumerable<PullRequest> pulls)
    {
        foreach (var pull in pulls.Where(p => p.State == PullRequestState.Open))
            Observe(null, pull);
    }

    /// <summary>A pull request is no longer tracked: it left the binding's branch.</summary>
    public void Forget(PullRequest pull)
    {
        _pending.TryRemove((pull.PipelineId, pull.Number), out _);
        _toAnnounce.TryRemove((pull.PipelineId, pull.Number), out _);
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

    /// <summary>
    /// Gives each new head "Waiting to start" when it will get a run on the active version: the pipeline declares CI
    /// and the head doesn't change the definition. A head that may change it gets nothing until its own build shows
    /// whether it keeps CI, since GitHub can't take a status back. One compare per new head.
    /// </summary>
    internal async Task AnnounceAsync(CancellationToken ct)
    {
        foreach (var (key, head) in _toAnnounce)
        {
            if (!_toAnnounce.TryRemove(new KeyValuePair<(Id<Pipeline>, int), string>(key, head)))
                continue;
            try
            {
                if (await WillRunOnActiveVersionAsync(key.Pipeline, key.Pull, head, ct)
                    && !runs.ListByPull(key.Pipeline, key.Pull).Any(r => r.HeadSha == head))
                    sp.GetRequiredService<CiStatusReporter>().Waiting(key.Pipeline, head);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not tell whether pull request #{Pull} in pipeline '{PipelineId}' gets a CI run", key.Pull, key.Pipeline);
            }
        }
    }

    private async Task<bool> WillRunOnActiveVersionAsync(Id<Pipeline> pipelineId, int number, string head, CancellationToken ct)
    {
        if (!sp.GetRequiredService<PullRequestService>().TryGet(pipelineId, number, out var pull)
            || pull.State != PullRequestState.Open || pull.HeadSha != head
            || runs.ListByPull(pipelineId, number).Any(r => r.HeadSha == head)
            || sp.GetRequiredService<PipelineConfigBindingService>().GetByPipelineId(pipelineId).TryPickProblems(out _, out var binding)
            || !sp.GetRequiredService<PipelineActivationService>().TryGet(pipelineId, out var activation)
            || activation is not PipelineActivation.Active active
            || !sp.GetRequiredService<PipelineVersionService>().TryGet(active.VersionId, out var version)
            || sp.GetRequiredService<CiPlanCache>().Get(version.Id).TryPickProblems(out _))
            return false;

        return !(await sp.GetRequiredService<CiRunStartService>().ChangesDefinitionAsync(binding, version, head, ct))
            .TryPickProblems(out _, out var changes) && !changes;
    }

    /// <summary>Starts every due run, one after another.</summary>
    internal async Task StartDueAsync(CancellationToken ct)
    {
        foreach (var (pipelineId, pull, head) in TakeDue())
        {
            try
            {
                var started = await sp.GetRequiredService<CiRunStartService>().StartAsync(pipelineId, pull, head, ct);
                if (started.TryPickProblems(out var problems, out var start))
                    logger.LogProblems(LogLevel.Warning, problems, "Could not start the CI run of pull request #{Pull} in pipeline '{PipelineId}'", pull, pipelineId);
                else
                    ReportSkip(pipelineId, head, start.Skipped);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not start the CI run of pull request #{Pull} in pipeline '{PipelineId}'", pull, pipelineId);
            }
        }
    }

    /// <summary>A head given "Waiting to start" that gets no run must not stay pending.</summary>
    private void ReportSkip(Id<Pipeline> pipelineId, string head, CiStartSkip? skipped)
    {
        var statuses = sp.GetRequiredService<CiStatusReporter>();
        if (skipped is CiStartSkip.NoCi)
            statuses.NoCi(pipelineId, head);
        else if (skipped is CiStartSkip.Conflict
                 && !sp.GetRequiredService<PipelineConfigBindingService>().GetByPipelineId(pipelineId).TryPickProblems(out _, out var binding))
            statuses.Conflict(pipelineId, head, binding.Branch);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var swept = time.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            await AnnounceAsync(stoppingToken);
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
