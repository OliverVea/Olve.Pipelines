using System.Globalization;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Versions;

namespace Olve.Pipelines.Pipelines.Retention;

/// <summary>One object in the bucket.</summary>
public sealed record StoredObject(string Key, DateTimeOffset LastModified, long Size);

/// <summary>A K8s Job labelled <c>app=olve-pipelines</c> (metadata + whether it finished).</summary>
public sealed record ClusterJob(string Name, IReadOnlyDictionary<string, string> Labels, DateTimeOffset Created, bool Finished, int? TtlSecondsAfterFinished);

/// <summary>A K8s Secret (metadata only — its data is never read).</summary>
public sealed record ClusterSecret(string Name, DateTimeOffset Created);

/// <summary>A snapshot of everything retention decides over. A null listing means "unavailable": that part is skipped.</summary>
/// <param name="PinnedRuns">Runs never expired whatever their age or rank — the run whose <c>publish-cli</c>
/// files <c>/download</c> serves (<see cref="Distribution.CliReleaseQuery"/>).</param>
/// <param name="Policies">Pipelines whose manifest overrides the instance thresholds (<see cref="RetentionPolicies"/>); others use the options.</param>
public sealed record RetentionInputs(
    DateTimeOffset Now,
    IReadOnlySet<Id<Pipeline>> Pipelines,
    IReadOnlyList<Run> Runs,
    IReadOnlyList<Attempt> Attempts,
    IReadOnlyList<PipelineVersion> Versions,
    IReadOnlyDictionary<Id<Pipeline>, PipelineActivation> Activations,
    IReadOnlyList<StoredObject>? Objects,
    IReadOnlyList<ClusterJob>? Jobs,
    IReadOnlyList<ClusterSecret>? Secrets,
    IReadOnlySet<Id<Run>>? PinnedRuns = null,
    IReadOnlyDictionary<Id<Pipeline>, RetentionPolicy>? Policies = null);

public enum RetentionCategory
{
    /// <summary>Objects of a run expired by this sweep (everything but <c>run.json</c>, which persistence removes).</summary>
    ExpiredRun,
    /// <summary>Objects of a run that no longer exists, older than the orphan grace.</summary>
    OrphanedRun,
    /// <summary>A leftover <c>result.tar.gz</c> of a finished (or unknown) attempt.</summary>
    ResultArchive,
    /// <summary>A <c>step.tar.gz</c> no active/draining version or retained run uses.</summary>
    Build,
    /// <summary>A stale <c>triggers/…/event.json</c>.</summary>
    TriggerEvent,
}

/// <param name="Group">What the object belongs to, for logging (<c>runs/&lt;p&gt;/12</c>, the build key, …).</param>
public sealed record ObjectDeletion(RetentionCategory Category, string Group, StoredObject Object);

/// <summary>What one sweep deletes (or, in dry-run, would delete), plus what it kept, for the summary log.</summary>
public sealed record RetentionPlan(
    IReadOnlyList<Run> ExpiredRuns,
    int RunsKept,
    IReadOnlyList<ObjectDeletion> Objects,
    int BuildsKept,
    IReadOnlyList<ClusterSecret> Secrets,
    IReadOnlyList<ClusterJob> Jobs);

/// <summary>
/// The retention decisions as one pure function over a snapshot (<see cref="RetentionOptions"/> has the
/// policy). Safety rules: never a Running run; never <c>run.json</c>, <c>manifest.json</c> or an index (the
/// persistence protocol owns those — a listed run whose <c>run.json</c> is gone fails startup); only keys of
/// the known shapes; Jobs only with a v2 role and a pipeline of this instance (the runner namespace is shared
/// with v1, whose Jobs are also named <c>olve-&lt;hex&gt;</c>); a URL Secret only once its Job is gone or
/// finished and its attempt (if it names one) is not active.
/// </summary>
public static class RetentionPlanner
{
    private const string RunStateFile = "run.json";
    private const string ResultArchiveFile = "result.tar.gz";
    private const string BinaryFile = "step.tar.gz";

    /// <summary>Roles v2 gives its Jobs (v1's bare failure-handler Jobs use <c>failure-handler</c>; its step Jobs have no role).</summary>
    private static readonly HashSet<string> V2Roles = new(StringComparer.Ordinal) { "step", "map-trigger", "build", "on-failure" };

    private static readonly string[] HelperJobPrefixes = ["olve-map-", "olve-fh-", "olve-build-"];

    public static RetentionPlan Plan(RetentionInputs inputs, RetentionOptions options)
    {
        var fallback = RetentionPolicy.Of(options);
        RetentionPolicy PolicyOf(Id<Pipeline> p) => inputs.Policies is { } policies && policies.TryGetValue(p, out var policy) ? policy : fallback;

        var (expired, kept) = SelectExpiredRuns(inputs.Runs, inputs.Now, PolicyOf, inputs.PinnedRuns);
        var expiredIds = expired.Select(r => r.Id).ToHashSet();
        var retainedRuns = inputs.Runs.Where(r => !expiredIds.Contains(r.Id)).ToList();
        var retainedAttempts = inputs.Attempts.Where(a => !expiredIds.Contains(a.RunId)).ToList();

        var objects = new List<ObjectDeletion>();
        var buildsKept = 0;
        if (inputs.Objects is { } listing)
        {
            objects.AddRange(RunObjects(listing, expired, retainedRuns, retainedAttempts, inputs.Now, options));
            var (builds, keptCount) = Builds(listing, inputs.Versions, retainedRuns, inputs.Activations, inputs.Now, p => PolicyOf(p).BuildGrace);
            objects.AddRange(builds);
            buildsKept = keptCount;
            objects.AddRange(listing
                .Where(o => IsTriggerEvent(o.Key) && inputs.Now - o.LastModified > options.OrphanGrace)
                .Select(o => new ObjectDeletion(RetentionCategory.TriggerEvent, o.Key, o)));
        }

        var jobs = inputs.Jobs is { } j ? Jobs(j, inputs.Pipelines, retainedAttempts, inputs.Now, options) : [];
        var secrets = inputs.Secrets is { } s && inputs.Jobs is { } allJobs
            ? Secrets(s, allJobs, retainedAttempts, inputs.Now, options.ClusterGrace)
            : [];

        return new RetentionPlan(expired, kept, objects, buildsKept, secrets, jobs);
    }

    /// <summary>
    /// Per pipeline, newest first: the first <paramref name="keepLatest"/> are kept; of the rest, a finished run
    /// whose last activity (finish, else creation) is older than <paramref name="minAge"/> expires, unless pinned.
    /// </summary>
    public static (IReadOnlyList<Run> Expired, int Kept) SelectExpiredRuns(
        IEnumerable<Run> runs, DateTimeOffset now, int keepLatest, TimeSpan minAge, IReadOnlySet<Id<Run>>? pinned = null)
    {
        var policy = new RetentionPolicy(keepLatest, minAge, TimeSpan.Zero);
        return SelectExpiredRuns(runs, now, _ => policy, pinned);
    }

    /// <summary>As above, with the thresholds taken per pipeline from <paramref name="policyOf"/>.</summary>
    public static (IReadOnlyList<Run> Expired, int Kept) SelectExpiredRuns(
        IEnumerable<Run> runs, DateTimeOffset now, Func<Id<Pipeline>, RetentionPolicy> policyOf, IReadOnlySet<Id<Run>>? pinned = null)
    {
        var all = runs.ToList();
        var expired = all
            .GroupBy(r => r.PipelineId)
            .SelectMany(g =>
            {
                var policy = policyOf(g.Key);
                return g.OrderByDescending(r => r.Number).Skip(Math.Max(0, policy.KeepLatestRuns))
                    .Where(r => r.Status.IsTerminal() && now - (r.FinishedAt ?? r.CreatedAt) > policy.MinRunAge);
            })
            .Where(r => pinned is null || !pinned.Contains(r.Id))
            .OrderBy(r => r.PipelineId).ThenBy(r => r.Number)
            .ToList();
        return (expired, all.Count - expired.Count);
    }

    private static IEnumerable<ObjectDeletion> RunObjects(
        IReadOnlyList<StoredObject> listing, IReadOnlyList<Run> expired, IReadOnlyList<Run> retained,
        IReadOnlyList<Attempt> retainedAttempts, DateTimeOffset now, RetentionOptions options)
    {
        var expiredKeys = expired.Select(r => (Hex(r.PipelineId), r.Number)).ToHashSet();
        var retainedKeys = retained.Select(r => (Hex(r.PipelineId), r.Number)).ToHashSet();
        var attempts = retainedAttempts.ToDictionary(a => (Hex(a.PipelineId), a.RunNumber, a.StepName, a.Number));

        var byRun = listing
            .Select(o => (Object: o, Parts: o.Key.Split('/')))
            .Where(x => x.Parts.Length >= 4 && x.Parts[0] == "runs" && IsHexId(x.Parts[1]) && IsRunNumber(x.Parts[2]))
            .GroupBy(x => (Pipeline: x.Parts[1], Number: int.Parse(x.Parts[2], CultureInfo.InvariantCulture)));

        foreach (var run in byRun)
        {
            var group = $"runs/{run.Key.Pipeline}/{run.Key.Number}";
            var deletable = run.Where(x => !(x.Parts.Length == 4 && x.Parts[3] == RunStateFile)).ToList();

            if (expiredKeys.Contains(run.Key))
            {
                foreach (var x in deletable)
                    yield return new ObjectDeletion(RetentionCategory.ExpiredRun, group, x.Object);
                continue;
            }

            if (!retainedKeys.Contains(run.Key))
            {
                // Not a run of this instance's state: only once nothing under it changed for the orphan grace.
                if (deletable.Count > 0 && now - run.Max(x => x.Object.LastModified) > options.OrphanGrace)
                    foreach (var x in deletable)
                        yield return new ObjectDeletion(RetentionCategory.OrphanedRun, group, x.Object);
                continue;
            }

            foreach (var x in deletable)
            {
                // runs/<p>/<n>/<step>/<a>/result.tar.gz of an attempt that is no longer active.
                if (x.Parts.Length != 6 || x.Parts[5] != ResultArchiveFile || !IsRunNumber(x.Parts[4]))
                    continue;
                var key = (run.Key.Pipeline, run.Key.Number, x.Parts[3], int.Parse(x.Parts[4], CultureInfo.InvariantCulture));
                var active = attempts.TryGetValue(key, out var attempt) && attempt.Status.IsActive();
                if (!active && now - x.Object.LastModified > options.ResultArchiveGrace)
                    yield return new ObjectDeletion(RetentionCategory.ResultArchive, group, x.Object);
            }
        }
    }

    private static (IReadOnlyList<ObjectDeletion> Deletions, int Kept) Builds(
        IReadOnlyList<StoredObject> listing, IReadOnlyList<PipelineVersion> versions, IReadOnlyList<Run> retainedRuns,
        IReadOnlyDictionary<Id<Pipeline>, PipelineActivation> activations, DateTimeOffset now, Func<Id<Pipeline>, TimeSpan> graceOf)
    {
        var byId = versions.ToDictionary(v => v.Id);
        var inUse = new HashSet<Id<PipelineVersion>>(retainedRuns.Select(r => r.VersionId));
        foreach (var activation in activations.Values)
        {
            switch (activation)
            {
                case PipelineActivation.Active a:
                    inUse.Add(a.VersionId);
                    break;
                case PipelineActivation.Draining d:
                    inUse.Add(d.FromVersionId);
                    inUse.Add(d.ToVersionId);
                    break;
            }
        }

        var keep = inUse.Where(byId.ContainsKey).Select(id => byId[id].BinaryKey).ToHashSet(StringComparer.Ordinal);
        var deletions = new List<ObjectDeletion>();
        var kept = 0;
        var graces = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        foreach (var o in listing.Where(o => IsBinary(o.Key)))
        {
            var pipelineHex = o.Key.Split('/')[1];
            if (!graces.TryGetValue(pipelineHex, out var grace))
                graces[pipelineHex] = grace = graceOf(new Id<Pipeline>(new Id(Guid.ParseExact(pipelineHex, "N"))));
            if (keep.Contains(o.Key) || now - o.LastModified <= grace)
                kept++;
            else
                deletions.Add(new ObjectDeletion(RetentionCategory.Build, o.Key, o));
        }

        return (deletions, kept);
    }

    private static IReadOnlyList<ClusterJob> Jobs(
        IReadOnlyList<ClusterJob> jobs, IReadOnlySet<Id<Pipeline>> pipelines, IReadOnlyList<Attempt> retainedAttempts,
        DateTimeOffset now, RetentionOptions options)
    {
        var mine = pipelines.Select(Hex).ToHashSet(StringComparer.Ordinal);
        var attempts = retainedAttempts.ToDictionary(a => a.KubernetesJobName, StringComparer.Ordinal);
        var result = new List<ClusterJob>();

        foreach (var job in jobs)
        {
            if (!job.Labels.TryGetValue("olve-role", out var role) || !V2Roles.Contains(role)
                || !job.Labels.TryGetValue("olve-pipeline", out var pipeline) || !mine.Contains(pipeline))
                continue;

            var age = now - job.Created;
            var leakedStep = role == "step" && !job.Finished && age > options.ClusterGrace
                             && !(attempts.TryGetValue(job.Name, out var attempt) && attempt.Status.IsActive());
            var stale = job.Finished && job.TtlSecondsAfterFinished is null && age > options.FinishedJobMaxAge;
            if (leakedStep || stale)
                result.Add(job);
        }

        return result;
    }

    private static IReadOnlyList<ClusterSecret> Secrets(
        IReadOnlyList<ClusterSecret> secrets, IReadOnlyList<ClusterJob> jobs, IReadOnlyList<Attempt> retainedAttempts,
        DateTimeOffset now, TimeSpan grace)
    {
        var jobsByName = jobs.ToDictionary(j => j.Name, StringComparer.Ordinal);
        var attempts = retainedAttempts.ToDictionary(a => a.Id.Value.Value.ToString("N"), StringComparer.Ordinal);
        var result = new List<ClusterSecret>();

        foreach (var secret in secrets)
        {
            if (now - secret.Created <= grace)
                continue;

            string jobName;
            if (secret.Name.StartsWith("olve-urls-", StringComparison.Ordinal) && IsHexId(secret.Name["olve-urls-".Length..]))
            {
                var attemptHex = secret.Name["olve-urls-".Length..];
                if (attempts.TryGetValue(attemptHex, out var attempt) && attempt.Status.IsActive())
                    continue;
                jobName = $"olve-{attemptHex}";
            }
            else if (secret.Name.EndsWith("-urls", StringComparison.Ordinal)
                     && HelperJobPrefixes.Any(p => secret.Name.StartsWith(p, StringComparison.Ordinal)))
            {
                jobName = secret.Name[..^"-urls".Length];
            }
            else
            {
                continue;
            }

            if (!jobsByName.TryGetValue(jobName, out var job) || job.Finished)
                result.Add(secret);
        }

        return result;
    }

    private static bool IsTriggerEvent(string key)
    {
        var parts = key.Split('/');
        return parts.Length == 4 && parts[0] == "triggers" && IsHexId(parts[1]) && IsHexId(parts[2]) && parts[3] == "event.json";
    }

    /// <summary><c>versions/&lt;p&gt;/&lt;n&gt;/step.tar.gz</c> or <c>versions/&lt;p&gt;/builds/&lt;sha&gt;/step.tar.gz</c>.</summary>
    private static bool IsBinary(string key)
    {
        var parts = key.Split('/');
        return parts[0] == "versions" && parts.Length >= 4 && IsHexId(parts[1]) && parts[^1] == BinaryFile
               && ((parts.Length == 4 && IsRunNumber(parts[2])) || (parts.Length == 5 && parts[2] == "builds"));
    }

    private static string Hex(Id<Pipeline> id) => id.Value.Value.ToString("N");

    private static bool IsHexId(string s) => s.Length == 32 && s.All(char.IsAsciiHexDigitLower);

    private static bool IsRunNumber(string s) => s.Length is > 0 and < 10 && s.All(char.IsAsciiDigit);
}
