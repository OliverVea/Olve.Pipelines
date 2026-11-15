using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Sync.ConfigSource;

namespace Olve.Pipelines.Pipelines.Runs;

/// <summary>What a run changes since the newest older run that succeeded.</summary>
/// <param name="Since">Null when no older run succeeded.</param>
/// <param name="Comparison">Null when either run wasn't started by a push, or when the commits couldn't be listed.</param>
public record RunChanges(int Number, Run? Since, CommitComparison? Comparison = null, string? CommitsError = null);

/// <summary>
/// The changes of a range of runs. Superseded, failed and cancelled runs never count as the run compared against,
/// so the commits they carried show up in the next run that succeeds. Commits come from the binding's repository.
/// </summary>
public class RunChangesService(
    PipelineService pipelines,
    RunService runs,
    PipelineConfigBindingService bindings,
    IConfigSource source,
    CommitComparisonCache cache,
    ILogger<RunChangesService> logger)
{
    public const int MaxRuns = 50;
    private const int Parallelism = 4;

    /// <summary>The newest run before <paramref name="run"/> that succeeded.</summary>
    public static Run? Baseline(IEnumerable<Run> runs, Run run)
        => runs.Where(r => r.Number < run.Number && r.Status is RunStatus.Succeeded).MaxBy(r => r.Number);

    /// <param name="from">Default: the latest 20 runs up to <paramref name="to"/>.</param>
    public async Task<Result<IReadOnlyList<RunChanges>>> GetAsync(Id<Pipeline> pipelineId, int? from, int? to, CancellationToken ct = default)
    {
        if (!pipelines.TryGet(pipelineId, out _))
            return new NotFoundProblem("Pipeline '{0}' not found.", pipelineId);

        var all = runs.ListByPipeline(pipelineId);
        if (all.Count == 0)
            return Array.Empty<RunChanges>();

        var hi = to ?? all[0].Number;
        var lo = from ?? Math.Max(1, hi - 19);
        if (lo > hi)
            return ValidationProblem.ForField("from", "from ({0}) is after to ({1}).", lo, hi);
        if (hi - lo + 1 > MaxRuns)
            return ValidationProblem.ForField("from", "At most {0} runs at a time; narrow the range.", MaxRuns);

        var binding = bindings.GetByPipelineId(pipelineId).TryPickProblems(out _, out var bound) ? null : bound;
        var selected = all.Where(r => r.Number >= lo && r.Number <= hi).ToList();
        var changes = new RunChanges[selected.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, selected.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
            async (i, token) => changes[i] = await ForRunAsync(all, selected[i], binding, token));
        return changes;
    }

    private async Task<RunChanges> ForRunAsync(IReadOnlyList<Run> all, Run run, PipelineConfigBinding? binding, CancellationToken ct)
    {
        var since = Baseline(all, run);
        var changes = new RunChanges(run.Number, since);
        if (since?.Trigger.Origin?.PushSha is not { } @base || run.Trigger.Origin?.PushSha is not { } head)
            return changes;
        if (binding is null)
            return changes with { CommitsError = "The pipeline isn't bound to a repository, so its commits can't be listed." };

        var compared = await cache.GetOrCompareAsync(binding, @base, head, () => source.CompareAsync(binding, @base, head, ct));
        if (compared.TryPickProblems(out var problems, out var comparison))
        {
            logger.LogProblems(LogLevel.Warning, problems, "Listing the commits of run #{RunNumber} of pipeline '{PipelineId}' ({Repo}) failed",
                run.Number, run.PipelineId, binding.Repo);
            return changes with { CommitsError = problems.First().Message };
        }

        return changes with { Comparison = comparison };
    }
}

/// <summary>
/// Comparisons of two commits never change, so successful ones are kept for the life of the process. A failure is
/// kept for <see cref="FailureHold"/>, so a page left open while GitHub refuses doesn't keep asking it.
/// </summary>
public class CommitComparisonCache(TimeProvider time)
{
    public static readonly TimeSpan FailureHold = TimeSpan.FromMinutes(2);
    private const int MaxEntries = 2000;

    private sealed record Entry(Result<CommitComparison> Result, DateTimeOffset? FailedAt);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Repo, string Base, string Head), Entry> _entries = new();

    public async Task<Result<CommitComparison>> GetOrCompareAsync(
        PipelineConfigBinding binding, string @base, string head, Func<Task<Result<CommitComparison>>> compare)
    {
        var key = (binding.Repo, @base, head);
        if (_entries.TryGetValue(key, out var cached) && (cached.FailedAt is not { } failedAt || time.GetUtcNow() - failedAt < FailureHold))
            return cached.Result;

        var compared = await compare();
        if (_entries.Count >= MaxEntries)
            _entries.Clear();
        _entries[key] = new Entry(compared, compared.Succeeded ? null : time.GetUtcNow());
        return compared;
    }
}
