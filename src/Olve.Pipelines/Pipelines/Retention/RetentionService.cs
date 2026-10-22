using Microsoft.Extensions.Hosting;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Distribution;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Shared;
using Olve.Pipelines.Shared.Persistence;

namespace Olve.Pipelines.Pipelines.Retention;

/// <summary>
/// Applies <see cref="RetentionPlanner"/> once shortly after startup and then every
/// <see cref="RetentionOptions.Interval"/>: snapshot the run domain, list <c>runs/</c>, <c>versions/</c> and
/// <c>triggers/</c> in the bucket and the runner namespace's Jobs and Secret names, plan, then (unless
/// <see cref="RetentionOptions.DryRun"/>) delete in a crash-safe order — expired runs through
/// <see cref="RunService.Delete"/> under the scheduling lock (cascading attempts; persistence drops their
/// <c>run.json</c> only after an index without them is written), a persistence flush, then their other objects,
/// leftover objects, Secrets and Jobs. Every sweep logs per-category counts; a dry run also logs each item it
/// would delete. Best-effort: a failed listing skips that part, a failed sweep is retried next interval.
/// Skipped entirely in Ephemeral storage mode.
/// </summary>
public class RetentionService(
    RetentionOptions options,
    StorageOptions storage,
    IServiceProvider services,
    TimeProvider time,
    ILogger<RetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Enabled || storage.Mode == StorageMode.Ephemeral)
        {
            logger.LogInformation("Retention disabled (Enabled={Enabled}, storage mode {StorageMode})", options.Enabled, storage.Mode);
            return;
        }

        logger.LogInformation(
            "Retention{DryRun}: runs kept unless older than {MinRunAgeDays} days AND beyond each pipeline's latest {KeepLatestRuns}; unused binaries after {BuildGraceDays} days; orphans after {OrphanGraceDays} days; every {IntervalHours} h",
            options.DryRun ? " (dry run)" : "", options.MinRunAge.TotalDays, options.KeepLatestRuns, options.BuildGrace.TotalDays,
            options.OrphanGrace.TotalDays, options.Interval.TotalHours);

        try
        {
            await Task.Delay(options.StartupDelay, time, ct);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await SweepAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Retention sweep failed; retrying next interval");
                }

                await Task.Delay(options.Interval, time, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    internal async Task<RetentionPlan> SweepAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var pipelines = sp.GetRequiredService<EntityStore<Pipeline>>();
        var objectStore = sp.GetService<IRetentionObjectStore>();
        var (inventory, ns) = Inventory(sp);

        // Listings first, the domain snapshot after: anything created while we listed is then protected.
        var objects = objectStore is null ? null : await ListObjectsAsync(objectStore, ct);
        var jobs = inventory is null ? null : await TryAsync("list Jobs", () => inventory.ListJobsAsync(ns, ct));
        var secrets = inventory is null ? null : await TryAsync("list Secrets", () => inventory.ListSecretsAsync(ns, ct));

        var inputs = new RetentionInputs(
            time.GetUtcNow(),
            pipelines.List().Select(p => p.Id).ToHashSet(),
            sp.GetRequiredService<EntityStore<Run>>().List(),
            sp.GetRequiredService<EntityStore<Attempt>>().List(),
            sp.GetRequiredService<EntityStore<PipelineVersion>>().List(),
            sp.GetRequiredService<AttachmentStore<Pipeline, PipelineActivation>>().GetAll(),
            objects, jobs, secrets,
            PinnedRuns(sp));

        var plan = RetentionPlanner.Plan(inputs, options);
        var names = pipelines.List().ToDictionary(p => p.Id, p => p.Name);
        LogItems(plan, names);

        if (!options.DryRun)
            await ApplyAsync(sp, plan, objectStore, ns, ct);

        LogSummary(plan, objects is null, jobs is null, secrets is null);
        return plan;
    }

    /// <summary>The run whose <c>pl</c> binaries <c>/download</c> serves — the same query the endpoint uses.</summary>
    private static IReadOnlySet<Id<Run>> PinnedRuns(IServiceProvider sp) =>
        sp.GetService<CliReleaseQuery>() is { } cli && cli.TryGetLatest(out var attempt)
            ? new HashSet<Id<Run>> { attempt.RunId }
            : new HashSet<Id<Run>>();

    private async Task ApplyAsync(IServiceProvider sp, RetentionPlan plan, IRetentionObjectStore? objectStore, string ns, CancellationToken ct)
    {
        if (plan.ExpiredRuns.Count > 0)
        {
            var runs = sp.GetRequiredService<RunService>();
            sp.GetRequiredService<RunSchedulingLock>().Run(() =>
            {
                foreach (var run in plan.ExpiredRuns)
                {
                    if (runs.TryGet(run.Id, out var current) && current.Status.IsTerminal()
                        && runs.Delete(run.Id).TryPickProblems(out var problems))
                        logger.LogProblems(LogLevel.Warning, problems, "Retention could not delete run #{RunNumber} ('{RunId}')", run.Number, run.Id);
                }
            });

            // The runs index must stop listing them before their objects go.
            if (sp.GetServices<IHostedService>().OfType<RunPersistenceService>().FirstOrDefault() is { } persistence)
                await persistence.FlushAsync(ct);
        }

        if (objectStore is not null && plan.Objects.Count > 0)
        {
            var failed = await objectStore.DeleteAsync(plan.Objects.Select(o => o.Object.Key).ToList(), ct);
            if (failed > 0)
                logger.LogWarning("Retention: {FailedCount} of {ObjectCount} objects could not be deleted", failed, plan.Objects.Count);
        }

        if (plan.Secrets.Count == 0 && plan.Jobs.Count == 0)
            return;

        var kubernetes = sp.GetRequiredService<IKubernetesClient>();
        foreach (var secret in plan.Secrets)
            await kubernetes.DeleteSecretAsync(ns, secret.Name, ct);
        foreach (var job in plan.Jobs)
            await kubernetes.DeleteJobAsync(ns, job.Name, ct);
    }

    private void LogItems(RetentionPlan plan, IReadOnlyDictionary<Id<Pipeline>, string> names)
    {
        var verb = options.DryRun ? "would delete" : "deleting";

        var runObjects = plan.Objects.Where(o => o.Category == RetentionCategory.ExpiredRun)
            .GroupBy(o => o.Group).ToDictionary(g => g.Key, g => (Count: g.Count(), Bytes: g.Sum(o => o.Object.Size)));
        foreach (var run in plan.ExpiredRuns)
        {
            var group = $"runs/{run.PipelineId.Value.Value:N}/{run.Number}";
            var (count, bytes) = runObjects.GetValueOrDefault(group);
            logger.LogInformation("Retention {Verb} run #{RunNumber} of pipeline '{PipelineName}' ('{PipelineId}'), {RunStatus}, finished {FinishedAt:o}: {ObjectCount} objects, {Bytes} bytes",
                verb, run.Number, names.GetValueOrDefault(run.PipelineId, "(deleted pipeline)"), run.PipelineId, run.Status,
                run.FinishedAt ?? run.CreatedAt, count, bytes);
        }

        foreach (var group in plan.Objects.Where(o => o.Category != RetentionCategory.ExpiredRun).GroupBy(o => (o.Category, o.Group)))
            logger.LogInformation("Retention {Verb} {Category} '{Group}': {ObjectCount} objects, {Bytes} bytes, last modified {LastModified:o}",
                verb, group.Key.Category, group.Key.Group, group.Count(), group.Sum(o => o.Object.Size), group.Max(o => o.Object.LastModified));

        foreach (var secret in plan.Secrets)
            logger.LogInformation("Retention {Verb} orphaned K8s Secret '{SecretName}' (created {Created:o})", verb, secret.Name, secret.Created);
        foreach (var job in plan.Jobs)
            logger.LogInformation("Retention {Verb} K8s Job '{JobName}' (role {Role}, finished {Finished}, created {Created:o})",
                verb, job.Name, job.Labels.GetValueOrDefault("olve-role"), job.Finished, job.Created);
    }

    private void LogSummary(RetentionPlan plan, bool objectsUnavailable, bool jobsUnavailable, bool secretsUnavailable)
    {
        int Count(RetentionCategory c) => plan.Objects.Count(o => o.Category == c);

        logger.LogInformation(
            "Retention sweep{DryRun}: runs {RunsKept} kept, {RunsExpired} expired; objects of expired runs {ExpiredRunObjects}, orphaned run objects {OrphanedRunObjects} ({OrphanedRuns} runs), result archives {ResultArchives}, binaries {BuildsDeleted} deleted / {BuildsKept} kept, trigger events {TriggerEvents}; {Bytes} bytes; K8s Secrets {Secrets}, Jobs {Jobs}{Unavailable}",
            options.DryRun ? " (dry run, nothing deleted)" : "",
            plan.RunsKept, plan.ExpiredRuns.Count,
            Count(RetentionCategory.ExpiredRun), Count(RetentionCategory.OrphanedRun),
            plan.Objects.Where(o => o.Category == RetentionCategory.OrphanedRun).Select(o => o.Group).Distinct().Count(),
            Count(RetentionCategory.ResultArchive), Count(RetentionCategory.Build), plan.BuildsKept, Count(RetentionCategory.TriggerEvent),
            plan.Objects.Sum(o => o.Object.Size), plan.Secrets.Count, plan.Jobs.Count,
            Unavailable(objectsUnavailable, jobsUnavailable, secretsUnavailable));
    }

    private static string Unavailable(bool objects, bool jobs, bool secrets)
    {
        var parts = new List<string>();
        if (objects) parts.Add("bucket");
        if (jobs) parts.Add("Jobs");
        if (secrets) parts.Add("Secrets");
        return parts.Count == 0 ? "" : $" (skipped, unavailable: {string.Join(", ", parts)})";
    }

    private async Task<IReadOnlyList<StoredObject>?> ListObjectsAsync(IRetentionObjectStore store, CancellationToken ct)
    {
        var all = new List<StoredObject>();
        foreach (var prefix in new[] { "runs/", "versions/", "triggers/" })
        {
            var listed = await TryAsync($"list '{prefix}'", () => store.ListAsync(prefix, ct));
            if (listed is null)
                return null;
            all.AddRange(listed);
        }

        return all;
    }

    private (IKubernetesInventory? Inventory, string Namespace) Inventory(IServiceProvider sp)
    {
        try
        {
            var inventory = sp.GetService<IKubernetesInventory>();
            var ns = sp.GetService<KubernetesOptions>()?.Namespace;
            return inventory is null || string.IsNullOrEmpty(ns) ? (null, "") : (inventory, ns);
        }
        catch (Exception ex)
        {
            logger.LogInformation("Retention: Kubernetes unavailable ({Message}); skipping Jobs and Secrets", ex.Message);
            return (null, "");
        }
    }

    private async Task<T?> TryAsync<T>(string what, Func<Task<T>> action) where T : class
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Retention could not {What}; skipping that part this sweep", what);
            return null;
        }
    }
}
