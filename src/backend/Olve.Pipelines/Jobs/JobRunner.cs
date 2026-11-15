using Olve.Pipelines.Pipelines.Ci;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.Jobs;

public class JobRunner(
    IServiceProvider sp,
    JobWatcherRegistry registry,
    JobDispatchSignal signal,
    ILogger<JobRunner> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    public const int DefaultMaxConcurrentJobs = 4;

    /// <summary>Step Jobs running at once, across all pipelines (<c>Jobs:MaxConcurrent</c>).</summary>
    public int MaxConcurrentJobs { get; init; } = DefaultMaxConcurrentJobs;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Job runner started: at most {MaxConcurrentJobs} step Jobs at once", MaxConcurrentJobs);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                DispatchTick();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception in JobRunner dispatch tick");
            }

            await signal.WaitAsync(PollInterval, ct);
        }
    }

    internal void DispatchTick() =>
        JobDispatch.Run([.. CdCandidates(), .. CiCandidates()], () => registry.ActiveCount < MaxConcurrentJobs);

    /// <summary>
    /// CI's attempts that want a watcher (Scheduled, or InProgress after a restart). None of CD's per-step rules
    /// apply; a draft pull request's attempts go after a ready one's.
    /// </summary>
    private List<JobCandidate> CiCandidates()
    {
        using var scope = sp.CreateScope();
        if (scope.ServiceProvider.GetService<CiAttemptService>() is not { } attempts)
            return [];
        var runs = scope.ServiceProvider.GetRequiredService<CiRunService>();
        var pulls = scope.ServiceProvider.GetRequiredService<PullRequestService>();
        return attempts.List()
            .Where(a => a.Status is Scheduled or InProgress && !registry.IsRunning(JobWatchKey.Of(a.Id)))
            .Select(a => new JobCandidate(PriorityOf(a, runs, pulls), a.CreatedAt, () =>
            {
                using var start = sp.CreateScope();
                start.ServiceProvider.GetRequiredService<IJobExecutor>().EnsureRunning(a.Id);
                return true;
            }))
            .ToList();
    }

    internal static JobPriority PriorityOf(CiAttempt attempt, CiRunService runs, PullRequestService pulls) =>
        runs.TryGet(attempt.RunId, out var run) && (pulls.TryGet(run.PipelineId, run.Pull, out var pull) ? pull.Draft : run.Draft)
            ? JobPriority.DraftPull
            : JobPriority.ReadyPull;

    /// <summary>
    /// CD's attempts that want a watcher: Scheduled ones, and InProgress ones whose watcher is gone (after a restart
    /// they reattach to their Job). A Scheduled attempt waits while another attempt of its (pipeline, step) is in
    /// flight, and a matrix cell while its step has <c>maxParallel</c> cells in flight.
    /// </summary>
    private List<JobCandidate> CdCandidates()
    {
        var attempts = GetActiveAttempts();
        var busyKeys = CollectInProgressKeys(attempts);
        var cellsInFlight = attempts
            .Where(a => a.Status is InProgress || registry.IsRunning(a.Id))
            .GroupBy(MatrixParallelism.GroupOf)
            .ToDictionary(g => g.Key, g => g.Count());

        return attempts
            .Where(a => a.Status is not Waiting && !registry.IsRunning(a.Id))
            .Select(a => new JobCandidate(JobPriority.Deploy, a.CreatedAt, () => TryStart(a, busyKeys, cellsInFlight)))
            .ToList();
    }

    private bool TryStart(Attempt attempt, HashSet<StepKey> busyKeys, Dictionary<(Id<Run> Run, string Step), int> cellsInFlight)
    {
        // The busy-key gate holds a Scheduled attempt behind an in-flight attempt for the same
        // (pipeline, step). It must NOT apply to an InProgress attempt: after a restart its watcher is
        // gone but it still owns its key (CollectInProgressKeys added it), so gating it here would
        // make it block itself forever and never reattach to its K8s Job — wedging the pipeline.
        if (attempt.Status is Scheduled && IsBusy(attempt, busyKeys))
        {
            logger.LogDebug(
                "Holding Scheduled attempt '{AttemptId}' of step '{StepName}' — another attempt is already InProgress for the same (pipeline, step) key",
                attempt.Id, attempt.StepName);
            return false;
        }

        using var scope = sp.CreateScope();
        if (attempt.Status is Scheduled && MatrixCells.TryParseName(attempt.StepName, out _)
            && scope.ServiceProvider.GetRequiredService<MatrixParallelism>().LimitOf(attempt) is { } limit)
        {
            var group = MatrixParallelism.GroupOf(attempt);
            var running = cellsInFlight.GetValueOrDefault(group);
            if (running >= limit)
            {
                logger.LogDebug("Holding Scheduled attempt '{AttemptId}' of cell '{StepName}': {Running} of its step's cells are in progress (maxParallel {MaxParallel})",
                    attempt.Id, attempt.StepName, running, limit);
                return false;
            }
            cellsInFlight[group] = running + 1;
        }

        ReserveKey(attempt, busyKeys);

        var executor = scope.ServiceProvider.GetRequiredService<IJobExecutor>();
        executor.EnsureRunning(attempt.Id);
        return true;
    }

    private List<Attempt> GetActiveAttempts()
    {
        using var scope = sp.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<JobQueueService>();
        return queue.GetActiveAttempts().ToList();
    }

    private static HashSet<StepKey> CollectInProgressKeys(IEnumerable<Attempt> attempts)
        => attempts.Where(j => j.Status is InProgress).Select(j => j.Key).ToHashSet();

    private static bool IsBusy(Attempt attempt, HashSet<StepKey> busyKeys) => busyKeys.Contains(attempt.Key);

    private static void ReserveKey(Attempt attempt, HashSet<StepKey> busyKeys) => busyKeys.Add(attempt.Key);
}
