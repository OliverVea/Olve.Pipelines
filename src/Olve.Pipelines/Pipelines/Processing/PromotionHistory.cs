using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines.Building;
using static Olve.Pipelines.Jobs.Job;
using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.Pipelines.Processing;

/// <summary>
/// What a pipeline's persisted job history says about its bundles, computed in one pass over the
/// pipeline's jobs: which bundles were built (and whether their production run fully succeeded),
/// the most recent production success, each processing step's most recent success, and which steps
/// have a job Scheduled or InProgress. Jobs and job groups are persisted (<c>jobs.json</c>), so this
/// survives a controller restart — unlike the in-memory <see cref="ArtifactBundle"/> records.
/// </summary>
public sealed class PromotionHistory
{
    private readonly Dictionary<Id<ArtifactBundle>, (PromotionBundle Bundle, bool Succeeded)> _builds = new();
    private readonly Dictionary<Id<ProcessingStep>, (Id<ArtifactBundle> Bundle, DateTimeOffset CompletedAt, DateTimeOffset CreatedAt)> _lastSuccess = new();
    private readonly Dictionary<Id<ProcessingStep>, List<ProcessingJob>> _active = new();

    /// <summary>The bundle of the production run that most recently completed with every job Done.</summary>
    public Id<ArtifactBundle>? LatestProductionSuccess { get; private set; }

    private PromotionHistory() { }

    public static PromotionHistory Build(IEnumerable<Job> pipelineJobs, Func<Id<JobGroup>, JobGroup?> getGroup)
    {
        var history = new PromotionHistory();
        var productionByGroup = new Dictionary<Id<JobGroup>, List<ProductionJob>>();

        foreach (var job in pipelineJobs)
        {
            switch (job)
            {
                case ProductionJob production:
                    if (!productionByGroup.TryGetValue(production.JobGroupId, out var list))
                        productionByGroup[production.JobGroupId] = list = [];
                    list.Add(production);
                    break;

                case ProcessingJob processing:
                    if (processing.Status is Scheduled or InProgress)
                    {
                        if (!history._active.TryGetValue(processing.ProcessingStepId, out var active))
                            history._active[processing.ProcessingStepId] = active = [];
                        active.Add(processing);
                    }
                    else if (processing.Status is Done done
                        && (!history._lastSuccess.TryGetValue(processing.ProcessingStepId, out var cur)
                            || (done.CompletedAt, processing.CreatedAt).CompareTo((cur.CompletedAt, cur.CreatedAt)) > 0))
                    {
                        history._lastSuccess[processing.ProcessingStepId] = (processing.ArtifactBundleId, done.CompletedAt, processing.CreatedAt);
                    }
                    break;
            }
        }

        DateTimeOffset? latestCompletedAt = null;
        DateTimeOffset latestBuiltAt = default;
        foreach (var (groupId, jobs) in productionByGroup)
        {
            if (getGroup(groupId) is not ProductionJobGroup group)
                continue;

            var succeeded = jobs.Count > 0 && jobs.All(j => j.Status is Done);
            var bundle = new PromotionBundle(group.ArtifactBundleId, group.CreatedAt);
            history._builds[group.ArtifactBundleId] = (bundle, succeeded);

            if (!succeeded)
                continue;

            var completedAt = jobs.Max(j => ((Done)j.Status).CompletedAt);
            if (latestCompletedAt is null || (completedAt, group.CreatedAt).CompareTo((latestCompletedAt.Value, latestBuiltAt)) > 0)
            {
                latestCompletedAt = completedAt;
                latestBuiltAt = group.CreatedAt;
                history.LatestProductionSuccess = group.ArtifactBundleId;
            }
        }

        return history;
    }

    /// <summary>The build record of a bundle produced by this pipeline (any outcome); null when unknown.</summary>
    public PromotionBundle? BuildOf(Id<ArtifactBundle> bundleId)
        => _builds.TryGetValue(bundleId, out var build) ? build.Bundle : null;

    /// <summary>The build record of a bundle whose production run fully succeeded; null otherwise.</summary>
    public PromotionBundle? SucceededBuildOf(Id<ArtifactBundle> bundleId)
        => _builds.TryGetValue(bundleId, out var build) && build.Succeeded ? build.Bundle : null;

    /// <summary>The bundle the step most recently ran successfully (newest <see cref="Done"/>).</summary>
    public Id<ArtifactBundle>? LastSuccessOf(Id<ProcessingStep> stepId)
        => _lastSuccess.TryGetValue(stepId, out var s) ? s.Bundle : null;

    /// <summary>The step's Scheduled/InProgress jobs (empty when none).</summary>
    public IReadOnlyList<ProcessingJob> ActiveJobsOf(Id<ProcessingStep> stepId)
        => _active.TryGetValue(stepId, out var jobs) ? jobs : [];

    /// <summary>
    /// Every bundle the promotion logic may still need: the latest production success, each step's
    /// last success, and the bundle of every Scheduled/InProgress processing job. Bundle retention
    /// keeps these regardless of age.
    /// </summary>
    public IEnumerable<Id<ArtifactBundle>> BundlesInUse()
    {
        if (LatestProductionSuccess is { } production)
            yield return production;
        foreach (var success in _lastSuccess.Values)
            yield return success.Bundle;
        foreach (var jobs in _active.Values)
        foreach (var job in jobs)
            yield return job.ArtifactBundleId;
    }
}
