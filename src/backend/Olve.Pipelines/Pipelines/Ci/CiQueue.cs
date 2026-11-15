using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Olve.Pipelines.GitHub;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Sync.ConfigSource;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Pipelines.Ci;

[JsonConverter(typeof(JsonStringEnumConverter<CiQueueState>))]
public enum CiQueueState
{
    Queued,
    Testing,
    Merged,
    Removed,
}

[JsonConverter(typeof(JsonStringEnumConverter<CiQueueVia>))]
public enum CiQueueVia
{
    Api,
    AutoMerge,
}

/// <summary>
/// A pull request's place in a pipeline's merge queue. Entries are ordered by <see cref="Sequence"/>; one entry per
/// pull request is unfinished at a time, and finished ones (merged or removed) stay listed for a day.
/// </summary>
/// <param name="HeadSha">The head it was enqueued at: a push to the pull request removes it.</param>
/// <param name="Runs">The queue runs that tested it, newest first.</param>
/// <param name="Reason">Why it was removed.</param>
/// <param name="FailedStep">For an entry removed by a failed queue run: the step that failed.</param>
public sealed record CiQueueEntry(
    Id<CiQueueEntry> Id,
    Id<Pipeline> PipelineId,
    int Pull,
    string Title,
    string Branch,
    string HeadSha,
    CiQueueState State,
    long Sequence,
    DateTimeOffset EnqueuedAt,
    CiQueueVia Via,
    string? EnqueuedBy,
    IReadOnlyList<int> Runs,
    DateTimeOffset? FinishedAt = null,
    string? Reason = null,
    string? FailedStep = null) : IHasId<Id<CiQueueEntry>>
{
    [JsonIgnore]
    public bool IsFinished => State is CiQueueState.Merged or CiQueueState.Removed;
}

/// <summary>
/// How many queued pull requests a pipeline's next queue run may take. A failed batch of more than one halves it, and
/// its pull requests stay suspects until each has merged or been removed; then it is back to <see cref="CiQueueService.MaxBatch"/>.
/// </summary>
public sealed record CiQueueLimit(Id<Pipeline> PipelineId, int Limit, int Suspects);

public class CiQueueEvents
{
    public Event<EntityAdded<CiQueueEntry, Id<CiQueueEntry>>> OnAdded { get; } = new();
    public Event<EntityUpdated<CiQueueEntry, Id<CiQueueEntry>>> OnUpdated { get; } = new();
    public Event<EntityDeleted<CiQueueEntry, Id<CiQueueEntry>>> OnDeleted { get; } = new();
}

/// <summary>The merge queue's entries and batch limits. Singleton: owns the indexes, the sequence and the limits.</summary>
public class CiQueueService(EntityStore<CiQueueEntry> store, IdProvider idProvider, TimeProvider time)
{
    /// <summary>At most this many pull requests are tested together.</summary>
    public const int MaxBatch = 5;

    /// <summary>Finished entries are listed this long.</summary>
    public static readonly TimeSpan FinishedListed = TimeSpan.FromDays(1);

    private readonly EntityStoreIndex<CiQueueEntry, Id<Pipeline>> _byPipeline = store.CreateIndex(e => e.PipelineId);
    private readonly Lock _gate = new();
    private readonly Dictionary<Id<Pipeline>, CiQueueLimit> _limits = [];

    public event Action<Id<Pipeline>>? LimitChanged;

    /// <summary>A pipeline's entries, in queue order (finished ones included).</summary>
    public IReadOnlyList<CiQueueEntry> ListByPipeline(Id<Pipeline> pipelineId) =>
        [.. _byPipeline.GetForKey(pipelineId).Select(id => store.TryGet(id, out var e) ? e : null).OfType<CiQueueEntry>().OrderBy(e => e.Sequence)];

    /// <summary>The entries still to merge, in queue order.</summary>
    public IReadOnlyList<CiQueueEntry> Unfinished(Id<Pipeline> pipelineId) => [.. ListByPipeline(pipelineId).Where(e => !e.IsFinished)];

    public IReadOnlyList<CiQueueEntry> List() => store.List();

    public bool TryGetUnfinished(Id<Pipeline> pipelineId, int pull, [NotNullWhen(true)] out CiQueueEntry? entry)
    {
        entry = Unfinished(pipelineId).FirstOrDefault(e => e.Pull == pull);
        return entry is not null;
    }

    /// <summary>The pull request's entry: its unfinished one, else its latest finished one.</summary>
    public CiQueueEntry? Latest(Id<Pipeline> pipelineId, int pull) =>
        ListByPipeline(pipelineId).Where(e => e.Pull == pull).OrderBy(e => e.IsFinished ? 0 : 1).ThenBy(e => e.Sequence).LastOrDefault();

    public Result<CiQueueEntry> Enqueue(PullRequest pull, CiQueueVia via, string? enqueuedBy)
    {
        lock (_gate)
        {
            if (TryGetUnfinished(pull.PipelineId, pull.Number, out _))
                return new ConflictProblem("Pull request #{0} is already in the merge queue.", pull.Number);
            var sequence = store.List().Select(e => e.Sequence).DefaultIfEmpty(0).Max() + 1;
            var entry = new CiQueueEntry(idProvider.Create<CiQueueEntry>(), pull.PipelineId, pull.Number, pull.Title, pull.Branch, pull.HeadSha,
                CiQueueState.Queued, sequence, time.GetUtcNow(), via, enqueuedBy, []);
            store.Set(entry);
            return entry;
        }
    }

    public CiQueueEntry Testing(CiQueueEntry entry, int run) =>
        Mutate(entry, e => e with { State = CiQueueState.Testing, Runs = [run, .. e.Runs] });

    public CiQueueEntry Requeue(CiQueueEntry entry) =>
        Mutate(entry, e => e.IsFinished ? e : e with { State = CiQueueState.Queued });

    public CiQueueEntry Merged(CiQueueEntry entry) =>
        Mutate(entry, e => e.IsFinished ? e : e with { State = CiQueueState.Merged, FinishedAt = time.GetUtcNow() });

    public CiQueueEntry Remove(CiQueueEntry entry, string reason, string? failedStep = null) =>
        Mutate(entry, e => e.IsFinished ? e : e with { State = CiQueueState.Removed, FinishedAt = time.GetUtcNow(), Reason = reason, FailedStep = failedStep });

    /// <summary>Drops finished entries listed longer than <see cref="FinishedListed"/>.</summary>
    public void Purge(Id<Pipeline> pipelineId)
    {
        var before = time.GetUtcNow() - FinishedListed;
        foreach (var entry in ListByPipeline(pipelineId).Where(e => e.IsFinished && e.FinishedAt < before))
            store.Delete(entry.Id).DiscardResult();
    }

    public void DeletePipeline(Id<Pipeline> pipelineId)
    {
        foreach (var entry in ListByPipeline(pipelineId))
            store.Delete(entry.Id).DiscardResult();
        lock (_gate) _limits.Remove(pipelineId);
    }

    public CiQueueLimit Limit(Id<Pipeline> pipelineId)
    {
        lock (_gate) return _limits.TryGetValue(pipelineId, out var limit) ? limit : new CiQueueLimit(pipelineId, MaxBatch, 0);
    }

    public IReadOnlyList<CiQueueLimit> Limits()
    {
        lock (_gate) return [.. _limits.Values];
    }

    /// <summary>A batch of <paramref name="size"/> failed: the next takes half of it, all of it suspect.</summary>
    public void Split(Id<Pipeline> pipelineId, int size) => SetLimit(new CiQueueLimit(pipelineId, Math.Max(1, (size + 1) / 2), size));

    /// <summary><paramref name="cleared"/> suspects merged or were removed; once none is left the limit is back to the maximum.</summary>
    public void Cleared(Id<Pipeline> pipelineId, int cleared)
    {
        var limit = Limit(pipelineId);
        var suspects = limit.Suspects - cleared;
        SetLimit(suspects <= 0 ? new CiQueueLimit(pipelineId, MaxBatch, 0) : limit with { Suspects = suspects });
    }

    /// <summary>Loading persisted limits.</summary>
    public void Restore(CiQueueLimit limit)
    {
        lock (_gate) _limits[limit.PipelineId] = limit;
    }

    private void SetLimit(CiQueueLimit limit)
    {
        lock (_gate)
        {
            if (limit.Limit == MaxBatch && limit.Suspects == 0)
                _limits.Remove(limit.PipelineId);
            else
                _limits[limit.PipelineId] = limit;
        }

        LimitChanged?.Invoke(limit.PipelineId);
    }

    private CiQueueEntry Mutate(CiQueueEntry entry, Func<CiQueueEntry, CiQueueEntry> change)
    {
        store.Mutate(entry.Id, change).DiscardResult();
        return store.TryGet(entry.Id, out var after) ? after : entry;
    }
}

/// <summary>What a write to GitHub came to.</summary>
public enum GitHubWriteOutcome
{
    Done,

    /// <summary><c>POST …/merges</c>: the head was already in the branch.</summary>
    NoChange,

    /// <summary><c>POST …/merges</c>: the head doesn't merge into the branch (409).</summary>
    Conflict,

    /// <summary>GitHub refused it (405, 409, 422): <see cref="GitHubWrite.Message"/> says why.</summary>
    Refused,

    /// <summary>403 or 404: the token can't write.</summary>
    Denied,

    /// <summary>A 5xx or a network failure: worth trying again.</summary>
    Retry,
}

public sealed record GitHubWrite(GitHubWriteOutcome Outcome, int? Code = null, string? Sha = null, string? Tree = null, string? Message = null);

/// <summary>What the merge queue reads and writes on GitHub (tests use a fake). Writes need Contents: write.</summary>
public interface ICiQueueGitHub
{
    /// <summary>The commit the branch points at.</summary>
    Task<Result<string>> BranchHeadAsync(PipelineConfigBinding binding, string branch, CancellationToken ct);

    Task<GitHubWrite> CreateBranchAsync(PipelineConfigBinding binding, string branch, string sha, CancellationToken ct);

    Task<GitHubWrite> DeleteBranchAsync(PipelineConfigBinding binding, string branch, CancellationToken ct);

    /// <summary>Merges <paramref name="head"/> into <paramref name="branch"/>: the merge commit and its tree.</summary>
    Task<GitHubWrite> MergeIntoAsync(PipelineConfigBinding binding, string branch, string head, string message, CancellationToken ct);

    /// <summary>Merges the pull request by rebase, provided its head is still <paramref name="headSha"/>.</summary>
    Task<GitHubWrite> MergePullAsync(PipelineConfigBinding binding, int number, string headSha, CancellationToken ct);
}

public class GitHubCiQueueClient(
    IKubernetesClient kubernetes,
    KubernetesOptions kubernetesOptions,
    IConfiguration configuration,
    ILogger<GitHubCiQueueClient> logger) : ICiQueueGitHub
{
    private readonly string _apiBase = GitHubEndpoints.ApiUrl(configuration);

    public async Task<Result<string>> BranchHeadAsync(PipelineConfigBinding binding, string branch, CancellationToken ct)
    {
        var token = await BindingTokens.GetAsync(kubernetes, kubernetesOptions, binding, logger, ct);
        using var client = new HttpClient();
        using var request = GitHubConfigSource.BuildRequest(HttpMethod.Get, $"{_apiBase}/repos/{binding.Repo}/git/ref/heads/{Escape(branch)}", token);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return new ResultProblem("GitHub returned {0} reading branch '{1}' of '{2}'.", (int)response.StatusCode, branch, binding.Repo);
        var reference = await response.Content.ReadFromJsonAsync(GitHubPullRequestJsonContext.Default.GitHubGitRef, ct);
        return reference is null ? new ResultProblem("GitHub returned no branch '{0}' of '{1}'.", branch, binding.Repo) : reference.Object.Sha;
    }

    public Task<GitHubWrite> CreateBranchAsync(PipelineConfigBinding binding, string branch, string sha, CancellationToken ct) =>
        SendAsync(binding, HttpMethod.Post, "git/refs",
            JsonContent.Create(new GitHubCreateRefRequest($"refs/heads/{branch}", sha), GitHubPullRequestJsonContext.Default.GitHubCreateRefRequest), ct);

    public Task<GitHubWrite> DeleteBranchAsync(PipelineConfigBinding binding, string branch, CancellationToken ct) =>
        SendAsync(binding, HttpMethod.Delete, $"git/refs/heads/{Escape(branch)}", null, ct);

    public async Task<GitHubWrite> MergeIntoAsync(PipelineConfigBinding binding, string branch, string head, string message, CancellationToken ct)
    {
        var written = await SendAsync(binding, HttpMethod.Post, "merges",
            JsonContent.Create(new GitHubMergeRequest(branch, head, message), GitHubPullRequestJsonContext.Default.GitHubMergeRequest), ct,
            async response => response.StatusCode == HttpStatusCode.Created
                ? await response.Content.ReadFromJsonAsync(GitHubPullRequestJsonContext.Default.GitHubMergeCommit, ct) is { } merge
                    ? new GitHubWrite(GitHubWriteOutcome.Done, 201, merge.Sha, merge.Commit.Tree.Sha)
                    : new GitHubWrite(GitHubWriteOutcome.Retry, 201)
                : new GitHubWrite(GitHubWriteOutcome.NoChange, (int)response.StatusCode));
        return written.Outcome == GitHubWriteOutcome.Refused && written.Code == 409 ? written with { Outcome = GitHubWriteOutcome.Conflict } : written;
    }

    public Task<GitHubWrite> MergePullAsync(PipelineConfigBinding binding, int number, string headSha, CancellationToken ct) =>
        SendAsync(binding, HttpMethod.Put, $"pulls/{number}/merge",
            JsonContent.Create(new GitHubMergePullRequest("rebase", headSha), GitHubPullRequestJsonContext.Default.GitHubMergePullRequest), ct);

    private async Task<GitHubWrite> SendAsync(PipelineConfigBinding binding, HttpMethod method, string path, HttpContent? content, CancellationToken ct,
        Func<HttpResponseMessage, Task<GitHubWrite>>? success = null)
    {
        var token = await BindingTokens.GetAsync(kubernetes, kubernetesOptions, binding, logger, ct);
        using var client = new HttpClient();
        using var request = GitHubConfigSource.BuildRequest(method, $"{_apiBase}/repos/{binding.Repo}/{path}", token);
        request.Content = content;
        try
        {
            using var response = await client.SendAsync(request, ct);
            var code = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
                return success is null ? new GitHubWrite(GitHubWriteOutcome.Done, code) : await success(response);
            if (code is 403 or 404)
                return new GitHubWrite(GitHubWriteOutcome.Denied, code);
            if (code >= 500 || code == 429)
                return new GitHubWrite(GitHubWriteOutcome.Retry, code);
            var message = await ReadMessageAsync(response, ct);
            return new GitHubWrite(GitHubWriteOutcome.Refused, code, Message: message);
        }
        catch (HttpRequestException)
        {
            return new GitHubWrite(GitHubWriteOutcome.Retry);
        }
    }

    private static async Task<string?> ReadMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return (await response.Content.ReadFromJsonAsync(GitHubPullRequestJsonContext.Default.GitHubMessage, ct))?.Message;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string Escape(string branch) => string.Join('/', branch.Split('/').Select(Uri.EscapeDataString));
}
