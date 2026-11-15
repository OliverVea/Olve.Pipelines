using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Threading.Channels;
using Olve.Pipelines.GitHub;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Sync.ConfigSource;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.Pipelines.Ci;

/// <summary>A commit status as GitHub takes it: <c>pending</c>, <c>success</c>, <c>failure</c> or <c>error</c>, in <see cref="Context"/>.</summary>
public sealed record CiCommitStatus(string State, string Description, string? TargetUrl, string Context = CiCommitStatus.CiContext)
{
    public const string CiContext = "olve/ci";
    public const string QueueContext = "olve/merge-queue";
    public const int MaxDescription = 140;

    internal static string Cap(string s) => s.Length <= MaxDescription ? s : s[..(MaxDescription - 1)] + "…";
}

/// <summary>Where a status links: the CI run page (<c>Links:BaseUrl</c>, else <c>Webhooks:PublicBaseUrl</c>; empty = no link).</summary>
public sealed record CiStatusOptions(string LinkBaseUrl);

/// <summary>
/// The <c>olve/ci</c> status a CI run stands for, or null when it reports nothing: a superseded run (the run that
/// replaced it reports), a run cancelled because its pull request closed or merged, a cancelled run of a head the
/// pull request has moved on from, and a run still building its own definition (a definition without CI must leave
/// no status, and GitHub can't take one back). A run of the current head cancelled otherwise reports <c>error</c>:
/// no result, which a new run replaces. Transient.
/// </summary>
public class CiStatusPlanner(CiRunViewService views, PullRequestService pulls, CiStatusOptions options)
{
    public CiCommitStatus? For(CiRun run)
    {
        if (run.IsQueue || run.Status is RunStatus.Superseded)
            return null;
        if (run.Status is RunStatus.Cancelled)
            return run.CancelReason is CiCancelReason.Closed or CiCancelReason.Merged
                   || !pulls.TryGet(run.PipelineId, run.Pull, out var pull) || pull.HeadSha != run.HeadSha
                ? null
                : new CiCommitStatus("error", "Cancelled", Link(run));

        var steps = views.Summarize(run).Steps.Where(s => s.Status is not Contract.RunStepStatus.Excluded).ToList();
        var url = Link(run);
        switch (run.Status)
        {
            case RunStatus.Succeeded:
                return new CiCommitStatus("success", $"Succeeded in {Duration(run.CreatedAt, run.FinishedAt ?? run.CreatedAt)}", url);
            case RunStatus.Failed:
                var failed = steps.Where(s => s.Status is Contract.RunStepStatus.Failed).Select(s => s.Name).ToList();
                var what = failed.Count > 0 ? string.Join(", ", failed) : run.Problems?.FirstOrDefault();
                return new CiCommitStatus("failure", Cap(what is null ? "Failed" : $"Failed: {what}"), url);
            default:
                if (run.Building || steps.Count == 0)
                    return null;
                var done = steps.Count(s => s.Status is Contract.RunStepStatus.Succeeded or Contract.RunStepStatus.Failed
                    or Contract.RunStepStatus.Skipped or Contract.RunStepStatus.Cancelled or Contract.RunStepStatus.NotRun);
                return new CiCommitStatus("pending", $"{done} of {steps.Count} steps done", url);
        }
    }

    private string? Link(CiRun run) => Link(options, run.PipelineId, run.Number);

    internal static string? Link(CiStatusOptions options, Id<Pipeline> pipelineId, int number) => string.IsNullOrWhiteSpace(options.LinkBaseUrl)
        ? null
        : string.Create(CultureInfo.InvariantCulture, $"{options.LinkBaseUrl.TrimEnd('/')}/pipeline/{pipelineId.Value.Value:D}/ci/run/{number}");

    internal static string Duration(DateTimeOffset from, DateTimeOffset to)
    {
        var t = to - from;
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}h{t.Minutes:00}m")
            : t.TotalMinutes >= 1
                ? string.Create(CultureInfo.InvariantCulture, $"{t.Minutes}m{t.Seconds:00}s")
                : string.Create(CultureInfo.InvariantCulture, $"{t.Seconds}s");
    }

    private static string Cap(string s) => CiCommitStatus.Cap(s);
}

/// <summary>The commit and context a status goes on: one in flight per key, and only the latest state is sent.</summary>
public readonly record struct CiStatusKey(Id<Pipeline> PipelineId, string Sha, string Context = CiCommitStatus.CiContext);

/// <summary>What posting a status came to.</summary>
public enum CommitStatusOutcome
{
    Posted,

    /// <summary>A 5xx or a network failure: worth trying again.</summary>
    Retry,

    /// <summary>403 or 404: the token can't write statuses to the repository.</summary>
    Denied,

    /// <summary>Any other 4xx: GitHub won't take this one.</summary>
    Rejected,
}

/// <summary>Posts commit statuses to GitHub (tests use a fake).</summary>
public interface ICommitStatusClient
{
    Task<(CommitStatusOutcome Outcome, int? Code)> PostAsync(PipelineConfigBinding binding, string sha, CiCommitStatus status, CancellationToken ct);
}

/// <summary>
/// Why a binding's statuses don't reach GitHub, shown in the binding's warnings until a status gets through again.
/// Singleton.
/// </summary>
public class CiStatusWarnings
{
    /// <summary>What a warning is about; each clears on its own.</summary>
    public const string Statuses = "statuses";
    public const string Contents = "contents";

    private readonly ConcurrentDictionary<(Id<Pipeline>, string), string> _warnings = new();

    public static string Denied(string repo, int code) =>
        $"CI can't post commit statuses to '{repo}' (GitHub returned {code}): the binding's token needs Commit statuses: write.";

    public static string ContentsDenied(string repo, int code) =>
        $"The merge queue can't write to '{repo}' (GitHub returned {code}): the binding's token needs Contents: write.";

    /// <summary>True when the warning is new or changed.</summary>
    public bool Set(Id<Pipeline> pipelineId, string warning) => Set(pipelineId, Statuses, warning);

    public bool Set(Id<Pipeline> pipelineId, string topic, string warning)
    {
        var changed = !_warnings.TryGetValue((pipelineId, topic), out var before) || before != warning;
        _warnings[(pipelineId, topic)] = warning;
        return changed;
    }

    /// <summary>True when there was a warning to clear.</summary>
    public bool Clear(Id<Pipeline> pipelineId) => Clear(pipelineId, Statuses);

    public bool Clear(Id<Pipeline> pipelineId, string topic) => _warnings.TryRemove((pipelineId, topic), out _);

    public IReadOnlyList<string> For(Id<Pipeline> pipelineId) =>
        [.. _warnings.Where(w => w.Key.Item1 == pipelineId).OrderBy(w => w.Key.Item2, StringComparer.Ordinal).Select(w => w.Value)];
}

/// <summary>
/// Sends CI runs' statuses to GitHub off the event path. <see cref="Offer"/> records the latest status per commit; one
/// delivery per commit runs at a time and always sends the latest it has, so intermediate states coalesce. A 5xx or
/// network failure is retried with backoff; 403/404 is not (the binding gets a warning). Singleton.
/// </summary>
public class CiStatusDelivery(
    IServiceProvider services,
    PipelineConfigBindingService bindings,
    CiStatusWarnings warnings,
    TimeProvider time,
    ILogger<CiStatusDelivery> logger) : BackgroundService
{
    internal static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(32), TimeSpan.FromSeconds(60)];

    private readonly Lock _lock = new();
    private readonly Dictionary<CiStatusKey, (CiCommitStatus Status, long Version)> _latest = [];
    private readonly HashSet<CiStatusKey> _inFlight = [];
    private readonly Channel<CiStatusKey> _ready = Channel.CreateUnbounded<CiStatusKey>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<CiStatusKey, TaskCompletionSource> _done = [];
    private readonly Dictionary<CiStatusKey, CiCommitStatus> _posted = [];
    private long _version;

    /// <summary>
    /// Sends <paramref name="status"/> and returns once it was delivered (or given up on): a merge needs its required
    /// status on GitHub first. A delivery of the same key in flight sends it; this then waits for that one.
    /// </summary>
    public async Task SendNowAsync(CiStatusKey key, CiCommitStatus status, CancellationToken ct)
    {
        Task? running = null;
        lock (_lock)
        {
            _latest[key] = (status, ++_version);
            if (!_inFlight.Add(key))
            {
                if (!_done.TryGetValue(key, out var waiter))
                    _done[key] = waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                running = waiter.Task;
            }
        }

        if (running is null)
            await DeliverAsync(key, ct);
        else
            await running.WaitAsync(ct);
    }

    private void Finished(CiStatusKey key)
    {
        _latest.Remove(key);
        _inFlight.Remove(key);
        if (_done.Remove(key, out var waiter))
            waiter.TrySetResult();
    }

    public void Offer(CiStatusKey key, CiCommitStatus status)
    {
        lock (_lock)
        {
            if (_latest.TryGetValue(key, out var held) ? held.Status == status : _posted.GetValueOrDefault(key) == status)
                return;
            _latest[key] = (status, ++_version);
            if (!_inFlight.Add(key))
                return;
        }

        _ready.Writer.TryWrite(key);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var key in _ready.Reader.ReadAllAsync(ct))
            _ = DeliverAsync(key, ct);
    }

    /// <summary>Delivers what is waiting, one key at a time, without the hosted loop (tests).</summary>
    internal async Task DrainAsync(CancellationToken ct = default)
    {
        while (_ready.Reader.TryRead(out var key))
            await DeliverAsync(key, ct);
    }

    /// <summary>Sends the key's latest status until it is posted (or can't be), then sends whatever came meanwhile.</summary>
    public async Task DeliverAsync(CiStatusKey key, CancellationToken ct)
    {
        try
        {
            var retries = 0;
            while (!ct.IsCancellationRequested)
            {
                CiCommitStatus status;
                long version;
                lock (_lock)
                    (status, version) = _latest[key];

                bool posted;
                lock (_lock)
                    posted = _posted.GetValueOrDefault(key) == status;
                var outcome = posted ? CommitStatusOutcome.Posted : await PostAsync(key, status, ct);
                if (outcome == CommitStatusOutcome.Posted)
                    lock (_lock)
                        _posted[key] = status;
                if (outcome == CommitStatusOutcome.Retry && retries < Backoff.Length)
                {
                    await Task.Delay(Backoff[retries++], time, ct);
                    continue;
                }

                retries = 0;
                lock (_lock)
                {
                    if (_latest[key].Version != version)
                        continue;
                    Finished(key);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Posting the CI status of {Sha} failed", key.Sha);
            lock (_lock)
                Finished(key);
        }
    }

    private async Task<CommitStatusOutcome> PostAsync(CiStatusKey key, CiCommitStatus status, CancellationToken ct)
    {
        if (bindings.GetByPipelineId(key.PipelineId).TryPickProblems(out _, out var binding))
            return CommitStatusOutcome.Rejected;

        var (outcome, code) = await services.GetRequiredService<ICommitStatusClient>().PostAsync(binding, key.Sha, status, ct);
        switch (outcome)
        {
            case CommitStatusOutcome.Posted:
                if (warnings.Clear(key.PipelineId))
                    logger.LogInformation("CI statuses reach '{Repo}' again", binding.Repo);
                break;
            case CommitStatusOutcome.Denied:
                var warning = CiStatusWarnings.Denied(binding.Repo, code ?? 403);
                if (warnings.Set(key.PipelineId, warning))
                    logger.LogWarning("CI can't post commit statuses to '{Repo}' (GitHub returned {Code}); the binding's token needs Commit statuses: write",
                        binding.Repo, code);
                break;
            case CommitStatusOutcome.Rejected:
                logger.LogWarning("GitHub refused the CI status '{State}' of {Sha} in '{Repo}' ({Code})", status.State, key.Sha, binding.Repo, code);
                break;
        }

        return outcome;
    }
}

/// <summary>
/// The heads given "Waiting to start" before their run existed: if no run comes (a conflict, no CI), that status
/// must be replaced. In memory: after a restart, the startup puts such heads back by announcing them again. Singleton.
/// </summary>
public class CiWaitingHeads
{
    private readonly ConcurrentDictionary<(Id<Pipeline>, string), byte> _heads = new();

    public void Add(Id<Pipeline> pipelineId, string head) => _heads[(pipelineId, head)] = 0;

    /// <summary>True when the head was waiting; it no longer is.</summary>
    public bool Take(Id<Pipeline> pipelineId, string head) => _heads.TryRemove((pipelineId, head), out _);
}

/// <summary>Turns CI run changes into statuses for <see cref="CiStatusDelivery"/>. Transient.</summary>
public class CiStatusReporter(CiRunService runs, CiStatusPlanner planner, CiStatusDelivery delivery, CiWaitingHeads waiting)
{
    public const string WaitingToStart = "Waiting to start";

    /// <summary>A pull request's new head that will get a run: <c>pending</c> until the run reports.</summary>
    public void Waiting(Id<Pipeline> pipelineId, string head)
    {
        waiting.Add(pipelineId, head);
        delivery.Offer(new CiStatusKey(pipelineId, head), new CiCommitStatus("pending", WaitingToStart, null));
    }

    /// <summary>A head that was waiting gets no run because it doesn't merge onto <paramref name="branch"/>.</summary>
    public void Conflict(Id<Pipeline> pipelineId, string head, string branch)
    {
        if (waiting.Take(pipelineId, head))
            delivery.Offer(new CiStatusKey(pipelineId, head), new CiCommitStatus("error", CiCommitStatus.Cap($"Doesn't merge onto '{branch}'"), null));
    }

    /// <summary>A head that was waiting gets no run because its definition (or the pipeline's) declares no CI.</summary>
    public void NoCi(Id<Pipeline> pipelineId, string head)
    {
        if (waiting.Take(pipelineId, head))
            delivery.Offer(new CiStatusKey(pipelineId, head), new CiCommitStatus("success", "No CI to run", null));
    }

    public void Report(CiRun run)
    {
        if (planner.For(run) is not { } status)
            return;
        waiting.Take(run.PipelineId, run.HeadSha);
        delivery.Offer(new CiStatusKey(run.PipelineId, run.HeadSha, status.Context), status);
    }

    public void Report(Id<CiRun> runId)
    {
        if (runs.TryGet(runId, out var run))
            Report(run);
    }

    /// <summary>At startup: the latest run of each pull request again, in case its last status was lost with the process.</summary>
    public void ReportLatest(IEnumerable<PullRequest> openPulls)
    {
        foreach (var pull in openPulls)
            if (runs.ListByPipeline(pull.PipelineId).Where(r => r.Pull == pull.Number && !r.IsQueue).MaxBy(r => r.Number) is { } latest)
                Report(latest);
    }
}

/// <summary>Posts to <c>POST /repos/{repo}/statuses/{sha}</c> with the binding's token.</summary>
public class GitHubCommitStatusClient(
    IKubernetesClient kubernetes,
    KubernetesOptions kubernetesOptions,
    IConfiguration configuration,
    ILogger<GitHubCommitStatusClient> logger) : ICommitStatusClient
{
    private readonly string _apiBase = GitHubEndpoints.ApiUrl(configuration);

    public async Task<(CommitStatusOutcome Outcome, int? Code)> PostAsync(PipelineConfigBinding binding, string sha, CiCommitStatus status, CancellationToken ct)
    {
        var token = await BindingTokens.GetAsync(kubernetes, kubernetesOptions, binding, logger, ct);
        using var client = new HttpClient();
        using var request = GitHubConfigSource.BuildRequest(HttpMethod.Post, $"{_apiBase}/repos/{binding.Repo}/statuses/{Uri.EscapeDataString(sha)}", token);
        request.Content = JsonContent.Create(
            new GitHubCommitStatusRequest(status.State, status.Description, status.Context, status.TargetUrl),
            GitHubPullRequestJsonContext.Default.GitHubCommitStatusRequest);
        try
        {
            using var response = await client.SendAsync(request, ct);
            var code = (int)response.StatusCode;
            return (response.IsSuccessStatusCode ? CommitStatusOutcome.Posted
                : code is 403 or 404 ? CommitStatusOutcome.Denied
                : code >= 500 || code == 429 ? CommitStatusOutcome.Retry
                : CommitStatusOutcome.Rejected, code);
        }
        catch (HttpRequestException)
        {
            return (CommitStatusOutcome.Retry, null);
        }
    }
}
