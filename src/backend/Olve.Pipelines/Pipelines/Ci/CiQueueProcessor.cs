using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Versions;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.Pipelines.Ci;

/// <summary>The <c>olve/merge-queue</c> status of a queue entry, on the head it was enqueued at. Transient.</summary>
public class CiQueueStatuses(CiStatusDelivery delivery, CiStatusOptions options)
{
    public CiCommitStatus For(CiQueueEntry entry)
    {
        var url = entry.Runs.Count > 0 ? CiStatusPlanner.Link(options, entry.PipelineId, entry.Runs[0]) : null;
        return entry.State switch
        {
            CiQueueState.Queued => new CiCommitStatus("pending", "Waiting in the merge queue", url, CiCommitStatus.QueueContext),
            CiQueueState.Testing => new CiCommitStatus("pending", $"Testing in queue run #{entry.Runs[0]}", url, CiCommitStatus.QueueContext),
            CiQueueState.Merged => Passed(entry),
            _ => new CiCommitStatus("failure", CiCommitStatus.Cap($"Removed: {entry.Reason}"), url, CiCommitStatus.QueueContext),
        };
    }

    /// <summary>What the entry's head gets just before it is merged: the check branch protection requires.</summary>
    public CiCommitStatus Passed(CiQueueEntry entry) => new("success",
        entry.Runs.Count > 0 ? $"Passed in queue run #{entry.Runs[0]}" : "Passed in the merge queue",
        entry.Runs.Count > 0 ? CiStatusPlanner.Link(options, entry.PipelineId, entry.Runs[0]) : null, CiCommitStatus.QueueContext);

    public void Report(CiQueueEntry entry)
    {
        // A merged entry's success was sent before the merge.
        if (entry.State is not CiQueueState.Merged)
            delivery.Offer(Key(entry), For(entry));
    }

    public Task SendPassedAsync(CiQueueEntry entry, CancellationToken ct) => delivery.SendNowAsync(Key(entry), Passed(entry), ct);

    private static CiStatusKey Key(CiQueueEntry entry) => new(entry.PipelineId, entry.HeadSha, CiCommitStatus.QueueContext);
}

/// <summary>
/// Puts pull requests into the merge queue and takes them out: through the API, by GitHub auto-merge, and when a
/// pull request moves on (a push, a close, a retarget, back to draft). Taking out an entry that is being tested
/// cancels its queue run and queues the rest of its batch again. Transient.
/// </summary>
public class CiQueueActions(
    CiQueueService queue,
    PullRequestService pulls,
    PipelineService pipelines,
    PipelineConfigBindingService bindings,
    PipelineActivationService activations,
    CiPlanCache plans,
    CiRunService runs,
    CiCancellationService cancellation,
    CiQueueStatuses statuses,
    CiQueueProcessor processor,
    ILogger<CiQueueActions> logger)
{
    public Result<CiQueueEntry> Enqueue(Id<Pipeline> pipelineId, int number, CiQueueVia via, string? enqueuedBy)
    {
        if (!pipelines.TryGet(pipelineId, out _))
            return new NotFoundProblem("Pipeline '{0}' not found.", pipelineId);
        return pulls.TryGet(pipelineId, number, out var pull)
            ? Enqueue(pull, via, enqueuedBy)
            : new NotFoundProblem("Pull request #{0} not found in pipeline '{1}'.", number, pipelineId);
    }

    /// <summary>Enqueues <paramref name="pull"/> as given: a store event carries it before the lookups see it.</summary>
    private Result<CiQueueEntry> Enqueue(PullRequest pull, CiQueueVia via, string? enqueuedBy)
    {
        var (pipelineId, number) = (pull.PipelineId, pull.Number);
        if (!pipelines.TryGet(pipelineId, out var pipeline))
            return new NotFoundProblem("Pipeline '{0}' not found.", pipelineId);
        if (bindings.GetByPipelineId(pipelineId).TryPickProblems(out _, out var binding))
            return new ConflictProblem("Pipeline '{0}' is not bound to a repository.", pipeline.Name);
        if (!DeclaresCi(pipelineId))
            return new ConflictProblem("Pipeline '{0}' declares no CI.", pipeline.Name);
        if (pull.State != PullRequestState.Open)
            return new ConflictProblem("Pull request #{0} is {1}.", number, pull.State.ToString().ToLowerInvariant());
        if (pull.Draft)
            return new ConflictProblem("Pull request #{0} is a draft.", number);
        if (pull.Mergeable == false)
            return new ConflictProblem("Pull request #{0} doesn't merge onto '{1}'.", number, binding.Branch);

        if (queue.Enqueue(pull, via, enqueuedBy).TryPickProblems(out var problems, out var entry))
            return problems;
        logger.LogInformation("Pull request #{Pull} of pipeline '{PipelineName}' entered the merge queue ({Via})", number, pipeline.Name, via);
        statuses.Report(entry);
        processor.Nudge();
        return entry;
    }

    public Result<CiQueueEntry> Dequeue(Id<Pipeline> pipelineId, int number, string by)
    {
        if (!pipelines.TryGet(pipelineId, out _))
            return new NotFoundProblem("Pipeline '{0}' not found.", pipelineId);
        if (!queue.TryGetUnfinished(pipelineId, number, out var entry))
            return new NotFoundProblem("Pull request #{0} is not in the merge queue.", number);
        return Remove(entry, $"dequeued by {by}");
    }

    /// <summary>A tracked pull request was added or changed.</summary>
    public void Observe(PullRequest? before, PullRequest after)
    {
        if (queue.TryGetUnfinished(after.PipelineId, after.Number, out var entry))
        {
            if (after.State == PullRequestState.Merged)
            {
                MergedOutside(entry);
                return;
            }

            var reason = after.State == PullRequestState.Closed ? "closed"
                : after.HeadSha != entry.HeadSha ? "pushed"
                : after.Draft ? "draft"
                : before?.AutoMerge == true && !after.AutoMerge && entry.Via == CiQueueVia.AutoMerge ? "auto-merge turned off"
                : null;
            if (reason is null)
                return;
            Remove(entry, reason).DiscardResult();
        }

        if (AsksForQueue(before, after) && Enqueue(after, CiQueueVia.AutoMerge, null).TryPickProblems(out var problems))
            logger.LogProblems(LogLevel.Information, problems, "Pull request #{Pull} has auto-merge on but can't enter the merge queue", after.Number);
    }

    /// <summary>
    /// Auto-merge asks for the queue when it is turned on, and again on a new head or out of draft, so a fix pushed
    /// after a failure goes back in; a pull request removed at the same head stays out.
    /// </summary>
    private static bool AsksForQueue(PullRequest? before, PullRequest after) =>
        after is { AutoMerge: true, State: PullRequestState.Open, Draft: false }
        && (before is null || !before.AutoMerge || before.HeadSha != after.HeadSha || before.Draft || before.State != PullRequestState.Open);

    /// <summary>A pull request is no longer tracked: it left the binding's branch.</summary>
    public void Forget(PullRequest pull)
    {
        if (queue.TryGetUnfinished(pull.PipelineId, pull.Number, out var entry))
            Remove(entry, "retargeted").DiscardResult();
    }

    /// <summary>Takes the entry out; a queue run testing it is cancelled and the rest of its batch queued again.</summary>
    public Result<CiQueueEntry> Remove(CiQueueEntry entry, string reason, string? failedStep = null)
    {
        var removed = queue.Remove(entry, reason, failedStep);
        queue.Cleared(entry.PipelineId, 1);
        statuses.Report(removed);
        logger.LogInformation("Pull request #{Pull} left the merge queue of pipeline '{PipelineId}': {Reason}", entry.Pull, entry.PipelineId, reason);
        if (entry.State == CiQueueState.Testing)
            CancelRunOf(entry);
        processor.Nudge();
        return removed;
    }

    /// <summary>The pull request merged: in landing that is the queue's own merge, otherwise it merged outside the queue.</summary>
    private void MergedOutside(CiQueueEntry entry)
    {
        if (entry.State == CiQueueState.Testing && RunOf(entry) is { Status: not RunStatus.Running })
            return;
        queue.Merged(entry);
        queue.Cleared(entry.PipelineId, 1);
        if (entry.State == CiQueueState.Testing)
            CancelRunOf(entry);
        processor.Nudge();
    }

    private void CancelRunOf(CiQueueEntry entry)
    {
        if (RunOf(entry) is not { } run)
            return;
        if (run.Status is RunStatus.Running)
            cancellation.End(run, RunStatus.Cancelled, CiCancelReason.Dequeued).DiscardResult();
        foreach (var other in queue.Unfinished(entry.PipelineId).Where(e => e.State == CiQueueState.Testing && e.Runs.FirstOrDefault() == run.Number))
            statuses.Report(queue.Requeue(other));
        processor.DeleteBranchLater(entry.PipelineId, run.Branch);
    }

    private CiRun? RunOf(CiQueueEntry entry) =>
        entry.Runs.Count > 0 && runs.TryGetByNumber(entry.PipelineId, entry.Runs[0], out var run) ? run : null;

    private bool DeclaresCi(Id<Pipeline> pipelineId) =>
        activations.TryGet(pipelineId, out var activation) && activation is PipelineActivation.Active active
        && !plans.Get(active.VersionId).TryPickProblems(out _);
}

/// <summary>
/// Drives each pipeline's merge queue, one step at a time per pipeline:
/// <list type="bullet">
/// <item>No batch in test: the queued pull requests at the head (up to the pipeline's batch limit) are merged one on
/// another onto the branch head in a temporary branch <c>olve-queue/&lt;run&gt;</c>, and a queue run tests its commit.</item>
/// <item>While it runs: the branch moving outside the queue supersedes it; its pull requests are tested again.</item>
/// <item>Green: each pull request gets <c>olve/merge-queue</c> success and is merged by rebase at the head it was
/// tested at, provided the branch is still at the tree the batch had before it; after each merge the branch must be at
/// the tree the batch had after it. Any difference stops landing and the rest is tested again.</item>
/// <item>Red: a batch of one is removed (its failed step named); a larger one is split in halves.</item>
/// </list>
/// Singleton: holds the per-pipeline gates and the branches still to delete.
/// </summary>
public class CiQueueProcessor(IServiceProvider sp, TimeProvider time, ILogger<CiQueueProcessor> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);

    /// <summary>How often a running queue run's base branch is read from GitHub to see whether it moved.</summary>
    internal static readonly TimeSpan BranchCheck = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<Id<Pipeline>, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<Id<Pipeline>, DateTimeOffset> _branchChecked = new();
    private readonly ConcurrentDictionary<(Id<Pipeline>, string), bool> _branchesToDelete = new();
    private readonly SemaphoreSlim _signal = new(0);

    public void Nudge()
    {
        if (_signal.CurrentCount == 0)
            _signal.Release();
    }

    public void DeleteBranchLater(Id<Pipeline> pipelineId, string branch) => _branchesToDelete[(pipelineId, branch)] = true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessAllAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Unhandled exception in the merge queue");
            }

            await Task.WhenAny(_signal.WaitAsync(stoppingToken), Task.Delay(Tick, time, stoppingToken));
        }
    }

    public async Task ProcessAllAsync(CancellationToken ct)
    {
        var queue = sp.GetRequiredService<CiQueueService>();
        foreach (var pipelineId in queue.List().Select(e => e.PipelineId).Concat(_branchesToDelete.Keys.Select(k => k.Item1)).Distinct().ToList())
            await ProcessAsync(pipelineId, ct);
    }

    public async Task ProcessAsync(Id<Pipeline> pipelineId, CancellationToken ct = default)
    {
        var gate = _gates.GetOrAdd(pipelineId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (sp.GetRequiredService<PipelineConfigBindingService>().GetByPipelineId(pipelineId).TryPickProblems(out _, out var binding))
                return;
            var step = new QueueStep(sp, binding, logger, this);
            await DeleteBranchesAsync(step, pipelineId, ct);
            await step.RunAsync(ct);
        }
        finally
        {
            gate.Release();
        }
    }

    internal bool BranchCheckDue(Id<Pipeline> pipelineId)
    {
        var now = time.GetUtcNow();
        if (_branchChecked.TryGetValue(pipelineId, out var last) && now - last < BranchCheck)
            return false;
        _branchChecked[pipelineId] = now;
        return true;
    }

    private async Task DeleteBranchesAsync(QueueStep step, Id<Pipeline> pipelineId, CancellationToken ct)
    {
        foreach (var (key, _) in _branchesToDelete.Where(k => k.Key.Item1 == pipelineId).ToList())
            if (await step.DeleteBranchAsync(key.Item2, ct))
                _branchesToDelete.TryRemove(key, out _);
    }

    /// <summary>One pass over one pipeline's queue.</summary>
    private sealed class QueueStep(IServiceProvider sp, PipelineConfigBinding binding, ILogger logger, CiQueueProcessor processor)
    {
        private readonly CiQueueService _queue = sp.GetRequiredService<CiQueueService>();
        private readonly CiRunService _runs = sp.GetRequiredService<CiRunService>();
        private readonly ICiQueueGitHub _github = sp.GetRequiredService<ICiQueueGitHub>();
        private readonly IPullRequestSource _source = sp.GetRequiredService<IPullRequestSource>();
        private readonly CiQueueStatuses _statuses = sp.GetRequiredService<CiQueueStatuses>();
        private readonly CiStatusWarnings _warnings = sp.GetRequiredService<CiStatusWarnings>();
        private Id<Pipeline> PipelineId => binding.PipelineId;

        public async Task RunAsync(CancellationToken ct)
        {
            _queue.Purge(PipelineId);
            var unfinished = _queue.Unfinished(PipelineId);
            if (unfinished.Count == 0)
                return;

            var testing = unfinished.Where(e => e.State == CiQueueState.Testing).ToList();
            if (testing.Count > 0)
            {
                var number = testing[0].Runs[0];
                if (!_runs.TryGetByNumber(PipelineId, number, out var run))
                {
                    Requeue(testing);
                    return;
                }

                switch (run.Status)
                {
                    case RunStatus.Running:
                        await CheckBaseAsync(run, ct);
                        return;
                    case RunStatus.Succeeded:
                        await LandAsync(run, ct);
                        return;
                    case RunStatus.Failed:
                        Split(run);
                        await DeleteBranchAsync(run.Branch, ct);
                        return;
                    default:
                        Requeue(testing);
                        await DeleteBranchAsync(run.Branch, ct);
                        return;
                }
            }

            await StartBatchAsync(unfinished, ct);
        }

        /// <summary>The base branch moved outside the queue while the batch was tested: the run is superseded, its pull requests tested again.</summary>
        private async Task CheckBaseAsync(CiRun run, CancellationToken ct)
        {
            if (!processor.BranchCheckDue(PipelineId))
                return;
            if ((await _github.BranchHeadAsync(binding, binding.Branch, ct)).TryPickProblems(out var problems, out var head))
            {
                logger.LogProblems(LogLevel.Warning, problems, "The merge queue could not read '{Branch}' of '{Repo}'", binding.Branch, binding.Repo);
                return;
            }

            if (head == run.MainSha)
                return;
            logger.LogInformation("'{Branch}' of '{Repo}' moved to {Head} while queue run #{RunNumber} tested; its pull requests are tested again",
                binding.Branch, binding.Repo, head, run.Number);
            sp.GetRequiredService<CiCancellationService>().End(run, RunStatus.Superseded).DiscardResult();
            Requeue(Batch(run));
            await DeleteBranchAsync(run.Branch, ct);
        }

        private async Task StartBatchAsync(IReadOnlyList<CiQueueEntry> unfinished, CancellationToken ct)
        {
            var activations = sp.GetRequiredService<PipelineActivationService>();
            if (!activations.TryGet(PipelineId, out var activation) || activation is not PipelineActivation.Active active
                || !sp.GetRequiredService<PipelineVersionService>().TryGet(active.VersionId, out var version))
                return;

            var batch = unfinished.Take(Math.Min(_queue.Limit(PipelineId).Limit, CiQueueService.MaxBatch)).ToList();
            if ((await _github.BranchHeadAsync(binding, binding.Branch, ct)).TryPickProblems(out var headProblems, out var head))
            {
                logger.LogProblems(LogLevel.Warning, headProblems, "The merge queue could not read '{Branch}' of '{Repo}'", binding.Branch, binding.Repo);
                return;
            }

            var number = _runs.Reserve(PipelineId);
            var branch = $"olve-queue/{number}";
            var created = await _github.CreateBranchAsync(binding, branch, head, ct);
            if (created.Outcome == GitHubWriteOutcome.Refused)
            {
                await DeleteBranchAsync(branch, ct);
                created = await _github.CreateBranchAsync(binding, branch, head, ct);
            }

            if (!Wrote(created, $"creating branch '{branch}'"))
                return;

            var pulls = new List<CiBatchPull>();
            var at = head;
            foreach (var entry in batch)
            {
                var merged = await _github.MergeIntoAsync(binding, branch, entry.HeadSha, $"Merge pull request #{entry.Pull} into merge-queue batch {number}", ct);
                if (merged.Outcome == GitHubWriteOutcome.Conflict)
                {
                    sp.GetRequiredService<CiQueueActions>().Remove(entry, "conflict").DiscardResult();
                    await DeleteBranchAsync(branch, ct);
                    return;
                }

                string sha, tree;
                if (merged.Outcome == GitHubWriteOutcome.NoChange)
                {
                    if ((await _source.GetCommitAsync(binding, at, ct)).TryPickProblems(out _, out var commit))
                    {
                        await DeleteBranchAsync(branch, ct);
                        return;
                    }

                    (sha, tree) = (at, commit.Tree.Sha);
                }
                else if (Wrote(merged, $"merging pull request #{entry.Pull} into '{branch}'") && merged is { Sha: { } s, Tree: { } t })
                {
                    (sha, tree) = (s, t);
                }
                else
                {
                    await DeleteBranchAsync(branch, ct);
                    return;
                }

                pulls.Add(new CiBatchPull(entry.Pull, entry.Title, entry.Branch, entry.HeadSha, sha, tree));
                at = sha;
            }

            var starts = sp.GetRequiredService<CiRunStartService>();
            if ((await starts.ChangesDefinitionAsync(binding, version, at, ct)).TryPickProblems(out var compareProblems, out var ownDefinition))
            {
                logger.LogProblems(LogLevel.Warning, compareProblems, "The merge queue could not compare batch {Number} of '{Repo}'", number, binding.Repo);
                await DeleteBranchAsync(branch, ct);
                return;
            }

            if (!ownDefinition && sp.GetRequiredService<CiPlanCache>().Get(version.Id).TryPickProblems(out _))
            {
                foreach (var entry in batch)
                    sp.GetRequiredService<CiQueueActions>().Remove(entry, "the pipeline declares no CI").DiscardResult();
                await DeleteBranchAsync(branch, ct);
                return;
            }

            var problems = new List<string>();
            var config = ownDefinition ? (null, null) : starts.PinVersionConfig(PipelineId, version, problems);
            var input = new JsonObject { ["repo"] = binding.Repo, ["sha"] = at }.ToJsonString();
            foreach (var entry in batch)
                _statuses.Report(_queue.Testing(entry, number));

            var run = sp.GetRequiredService<CiSchedulingLock>().Run(() =>
            {
                var queueRun = _runs.Create(new CiRunRequest(PipelineId, 0, $"Merge queue batch {number}", branch, false, at, at, pulls[^1].Tree, head,
                    version, input, config.Snapshot, config.Config, problems, ownDefinition, CiRunKind.Queue, pulls, number));
                sp.GetRequiredService<CiScheduler>().Advance(queueRun.Id);
                return queueRun;
            });
            sp.GetRequiredService<CiDefinitionService>().BuildFor(run);
            logger.LogInformation("Queue run #{RunNumber} of pipeline '{PipelineId}' tests {Pulls} on {Head}",
                number, PipelineId, string.Join(", ", pulls.Select(p => $"#{p.Number}")), head);
        }

        /// <summary>Merges the batch's pull requests in order, each only while the branch is where the batch expects it.</summary>
        private async Task LandAsync(CiRun run, CancellationToken ct)
        {
            var batch = run.Batch ?? [];
            for (var i = 0; i < batch.Count; i++)
            {
                var pull = batch[i];
                var entry = _queue.ListByPipeline(PipelineId).LastOrDefault(e => e.Pull == pull.Number && e.Runs.FirstOrDefault() == run.Number);
                if (entry is { State: CiQueueState.Merged })
                    continue;
                if (entry is not { State: CiQueueState.Testing })
                {
                    // Left the queue: what was tested after it can't land as tested.
                    Requeue(Batch(run));
                    await DeleteBranchAsync(run.Branch, ct);
                    return;
                }

                if ((await _source.GetAsync(binding, pull.Number, ct)).TryPickProblems(out var readProblems, out var reported))
                {
                    logger.LogProblems(LogLevel.Warning, readProblems, "The merge queue could not read pull request #{Pull}", pull.Number);
                    return;
                }

                if (reported is null || reported is { State: "closed", MergedAt: null })
                {
                    sp.GetRequiredService<CiQueueActions>().Remove(entry, "closed").DiscardResult();
                    Requeue(Batch(run));
                    await DeleteBranchAsync(run.Branch, ct);
                    return;
                }

                if (reported.MergedAt is null)
                {
                    if (reported.Head.Sha != pull.HeadSha)
                    {
                        sp.GetRequiredService<CiQueueActions>().Remove(entry, "pushed").DiscardResult();
                        Requeue(Batch(run));
                        await DeleteBranchAsync(run.Branch, ct);
                        return;
                    }

                    var expected = i == 0 ? null : batch[i - 1].Tree;
                    if (!await BaseIsAsync(run, expected, ct))
                    {
                        logger.LogInformation("'{Branch}' of '{Repo}' moved outside the merge queue; queue run #{RunNumber}'s remaining pull requests are tested again",
                            binding.Branch, binding.Repo, run.Number);
                        Requeue(Batch(run));
                        await DeleteBranchAsync(run.Branch, ct);
                        return;
                    }

                    await _statuses.SendPassedAsync(entry, ct);
                    var merge = await _github.MergePullAsync(binding, pull.Number, pull.HeadSha, ct);
                    if (merge.Outcome is GitHubWriteOutcome.Retry)
                        return;
                    if (merge.Outcome is not GitHubWriteOutcome.Done && !await MergedAsync(pull.Number, ct))
                    {
                        if (merge.Outcome is GitHubWriteOutcome.Denied)
                            Denied(merge.Code);
                        var reason = merge.Outcome is GitHubWriteOutcome.Denied
                            ? "the binding's token can't merge (Contents: write)"
                            : $"GitHub refused the merge: {merge.Message ?? $"HTTP {merge.Code}"}";
                        sp.GetRequiredService<CiQueueActions>().Remove(entry, reason).DiscardResult();
                        Requeue(Batch(run));
                        await DeleteBranchAsync(run.Branch, ct);
                        return;
                    }
                }

                _queue.Merged(entry);
                _queue.Cleared(PipelineId, 1);
                logger.LogInformation("The merge queue merged pull request #{Pull} of '{Repo}' (queue run #{RunNumber})", pull.Number, binding.Repo, run.Number);

                if (!await BaseIsAsync(run, pull.Tree, ct))
                {
                    logger.LogWarning("'{Branch}' of '{Repo}' is not at the tree queue run #{RunNumber} tested after merging #{Pull}; the rest is tested again",
                        binding.Branch, binding.Repo, run.Number, pull.Number);
                    Requeue(Batch(run));
                    await DeleteBranchAsync(run.Branch, ct);
                    return;
                }
            }

            await DeleteBranchAsync(run.Branch, ct);
        }

        /// <summary>The branch is at the run's base (<paramref name="tree"/> null) or at a commit with <paramref name="tree"/>.</summary>
        private async Task<bool> BaseIsAsync(CiRun run, string? tree, CancellationToken ct)
        {
            if ((await _github.BranchHeadAsync(binding, binding.Branch, ct)).TryPickProblems(out _, out var head))
                return false;
            if (tree is null)
                return head == run.MainSha;
            return !(await _source.GetCommitAsync(binding, head, ct)).TryPickProblems(out _, out var commit) && commit.Tree.Sha == tree;
        }

        private async Task<bool> MergedAsync(int number, CancellationToken ct) =>
            !(await _source.GetAsync(binding, number, ct)).TryPickProblems(out _, out var reported) && reported?.MergedAt is not null;

        /// <summary>A failed batch: one pull request is removed with the step that failed; more are split in halves.</summary>
        private void Split(CiRun run)
        {
            var testing = Batch(run);
            if (testing.Count == 0)
                return;
            if ((run.Batch?.Count ?? testing.Count) > 1)
            {
                logger.LogInformation("Queue run #{RunNumber} of {Count} pull requests failed; the batch is split", run.Number, testing.Count);
                _queue.Split(PipelineId, testing.Count);
                Requeue(testing);
                return;
            }

            var attempts = sp.GetRequiredService<CiAttemptService>();
            var failed = attempts.ListByRun(run.Id).FirstOrDefault(a => a.Status is Failed)?.StepName;
            var step = failed is null ? null : MatrixStepOf(failed);
            sp.GetRequiredService<CiQueueActions>().Remove(testing[0], step is null ? "its definition failed" : $"{step} failed", step).DiscardResult();
        }

        private static string MatrixStepOf(string unit) => unit.IndexOf('[') is var i and > 0 ? unit[..i] : unit;

        /// <summary>The run's entries still being tested, in batch order.</summary>
        private List<CiQueueEntry> Batch(CiRun run) =>
            [.. _queue.Unfinished(PipelineId).Where(e => e.State == CiQueueState.Testing && e.Runs.FirstOrDefault() == run.Number)];

        private void Requeue(IEnumerable<CiQueueEntry> entries)
        {
            foreach (var entry in entries.ToList())
                _statuses.Report(_queue.Requeue(entry));
        }

        public async Task<bool> DeleteBranchAsync(string branch, CancellationToken ct)
        {
            var deleted = await _github.DeleteBranchAsync(binding, branch, ct);
            return deleted.Outcome is not GitHubWriteOutcome.Retry;
        }

        private bool Wrote(GitHubWrite write, string what)
        {
            switch (write.Outcome)
            {
                case GitHubWriteOutcome.Done:
                case GitHubWriteOutcome.NoChange:
                    if (_warnings.Clear(PipelineId, CiStatusWarnings.Contents))
                        logger.LogInformation("The merge queue can write to '{Repo}' again", binding.Repo);
                    return true;
                case GitHubWriteOutcome.Denied:
                    Denied(write.Code);
                    return false;
                default:
                    logger.LogWarning("The merge queue failed {What} in '{Repo}': {Outcome} {Code} {Message}", what, binding.Repo, write.Outcome, write.Code, write.Message);
                    return false;
            }
        }

        private void Denied(int? code)
        {
            if (_warnings.Set(PipelineId, CiStatusWarnings.Contents, CiStatusWarnings.ContentsDenied(binding.Repo, code ?? 403)))
                logger.LogWarning("The merge queue can't write to '{Repo}' (GitHub returned {Code}); the binding's token needs Contents: write", binding.Repo, code);
        }
    }
}
