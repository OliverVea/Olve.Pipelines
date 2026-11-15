using Microsoft.Extensions.Hosting;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Pipelines.Ci;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Retention;
using Olve.Pipelines.Pipelines.Runs;

namespace Olve.Pipelines.Jobs;

/// <summary>
/// At startup, deletes the unfinished step Jobs nothing will stop: a watcher deletes a cancelled attempt's Job,
/// but a shutdown ends the watcher and a terminal attempt gets no new one. That is a Job whose attempt is
/// terminal, or, for a pipeline of this instance, whose attempt doesn't exist: an attempt is saved before its
/// Job is created, so no saved state will ever own it. A Job younger than this process proves nothing, because
/// the pod being replaced in a rolling update may have just made it. Another instance's Jobs in the namespace stay.
/// CI attempts' Jobs are judged the same way against the CI attempts.
/// </summary>
public class LeftoverJobCleanupService(
    IKubernetesInventory inventory,
    IKubernetesClient kubernetes,
    KubernetesOptions options,
    EntityStore<Pipeline> pipelines,
    EntityStore<Attempt> attempts,
    EntityStore<CiAttempt> ciAttempts,
    TimeProvider time,
    ILogger<LeftoverJobCleanupService> logger) : BackgroundService
{
    private const string JobPrefix = "olve-";
    private static readonly DateTimeOffset ProcessStart = System.Diagnostics.Process.GetCurrentProcess().StartTime;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    public static IReadOnlyList<ClusterJob> Leftovers(
        IEnumerable<ClusterJob> jobs, IReadOnlySet<Id<Pipeline>> pipelines, IEnumerable<Attempt> attempts, DateTimeOffset processStart)
        => Leftovers(jobs, pipelines, attempts.ToDictionary(a => a.KubernetesJobName, a => a.Status.IsActive(), StringComparer.Ordinal), processStart);

    /// <param name="owned">Each Job name an attempt of this instance owns, of any kind, and whether that attempt is still active.</param>
    public static IReadOnlyList<ClusterJob> Leftovers(
        IEnumerable<ClusterJob> jobs, IReadOnlySet<Id<Pipeline>> pipelines, IReadOnlyDictionary<string, bool> owned, DateTimeOffset processStart)
    {
        var mine = pipelines.Select(p => p.Value.Value.ToString("N")).ToHashSet(StringComparer.Ordinal);
        return jobs.Where(job => !job.Finished
                                 && job.Labels.GetValueOrDefault("olve-role") is HarnessJobBuilder.StepRole or HarnessJobBuilder.CiStepRole
                                 && job.Labels.GetValueOrDefault("olve-pipeline") is { } pipeline && mine.Contains(pipeline)
                                 && (owned.TryGetValue(job.Name, out var active) ? !active : job.Created < processStart))
            .ToList();
    }

    /// <summary>A CD Job <c>olve-&lt;x&gt;</c> has <c>olve-urls-&lt;x&gt;</c>; a CI Job <c>olve-ci-&lt;x&gt;</c> has <c>olve-ci-&lt;x&gt;-urls</c>.</summary>
    internal static string UrlSecretOf(string jobName) => jobName.StartsWith(CiAttempt.JobPrefix, StringComparison.Ordinal)
        ? $"{jobName}-urls"
        : $"olve-urls-{jobName[JobPrefix.Length..]}";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await DeleteLeftoversAsync(ct);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not look for leftover K8s Jobs; retrying");
                await Task.Delay(RetryDelay, time, ct);
            }
        }
    }

    /// <remarks>Lists first: a Job created after the listing isn't judged against attempts read before it.</remarks>
    internal async Task DeleteLeftoversAsync(CancellationToken ct)
    {
        var jobs = await inventory.ListJobsAsync(options.Namespace, ct);
        var owned = attempts.List().ToDictionary(a => a.KubernetesJobName, a => a.Status.IsActive(), StringComparer.Ordinal);
        foreach (var ci in ciAttempts.List())
            owned[ci.KubernetesJobName] = ci.Status.IsActive();
        var leftovers = Leftovers(jobs, pipelines.List().Select(p => p.Id).ToHashSet(), owned, ProcessStart);

        foreach (var job in leftovers)
        {
            logger.LogWarning("Deleting leftover K8s Job '{JobName}' of pipeline '{PipelineId}': its attempt has ended or doesn't exist",
                job.Name, job.Labels.GetValueOrDefault("olve-pipeline"));
            await kubernetes.DeleteJobAsync(options.Namespace, job.Name, ct);
            await kubernetes.DeleteSecretAsync(options.Namespace, UrlSecretOf(job.Name), ct);
        }
    }
}
