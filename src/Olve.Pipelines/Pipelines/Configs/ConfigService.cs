using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Configs;

public record ConfigSnapshotInfo(int Number, string Author, DateTimeOffset CreatedAt, int? RestoredFrom)
{
    public static ConfigSnapshotInfo From(ConfigSnapshot s) => new(s.Number, s.Author, s.CreatedAt, s.RestoredFrom);
}

/// <summary>A config document: the snapshot's YAML as written (empty, with no snapshot, before the first save).</summary>
public record ConfigDocument(ConfigSnapshotInfo? Snapshot, string Yaml);

/// <summary>A config document checked against one version.</summary>
/// <param name="Satisfied">Valid and every referenced secret set: the version can activate.</param>
/// <param name="Effective">The effective config (JSON: defaults applied, declared entries only); null when invalid.</param>
public record ConfigCheckResponse(
    int Version,
    bool Valid,
    bool Satisfied,
    string[] Problems,
    string[] ReferencedSecrets,
    string[] MissingSecrets,
    ChangedDefault[] ChangedDefaults,
    string? Effective)
{
    public static ConfigCheckResponse From(ConfigCheck c) => new(c.VersionNumber, c.Valid, c.Satisfied, c.Problems.ToArray(),
        c.ReferencedSecrets.ToArray(), c.MissingSecrets.ToArray(), c.ChangedDefaults.ToArray(), c.Effective?.ToJsonString());
}

/// <param name="Active">The saved document checked against the active version (null = no version yet).</param>
/// <param name="Pending">Against the pending version, if one is waiting (it activates on its own once satisfied).</param>
/// <param name="ActivatedVersion">The pending version this save activated.</param>
public record ConfigSaveResponse(ConfigSnapshotInfo Snapshot, ConfigCheckResponse? Active, ConfigCheckResponse? Pending, int? ActivatedVersion);

public record SetConfigRequest(string Yaml);

public record RollbackConfigRequest(int Snapshot);

/// <summary>The pending version, as the binding status and health views show it.</summary>
/// <param name="Held">The pending version is the active one: new runs wait until its config is satisfied again.</param>
public record PendingVersionInfo(int Version, DateTimeOffset Since, bool Held, string[] Reasons, ChangedDefault[] ChangedDefaults)
{
    public static PendingVersionInfo From(PendingVersion p, bool held)
        => new(p.VersionNumber, p.Since, held, p.Reasons.ToArray(), p.ChangedDefaults.ToArray());
}

/// <summary>
/// Saving, rolling back and checking a pipeline's config document. A save is validated against the active
/// version (a document it rejects is not saved, so the running setup can't be broken by a typo); the check
/// against a pending version is reported alongside without blocking, and the gate re-runs after every save.
/// </summary>
public class ConfigService(
    PipelineService pipelines,
    PipelineVersionService versions,
    PipelineActivationService activations,
    ConfigSnapshotService snapshots,
    ConfigActivationService gate,
    IPipelineSecretNames secretNames,
    IServiceProvider services,
    ILogger<ConfigService> logger)
{
    public Result<ConfigDocument> Get(Id<Pipeline> pipelineId, int? snapshotNumber)
    {
        if (!pipelines.TryGet(pipelineId, out _))
            return new NotFoundProblem("Pipeline '{0}' not found.", pipelineId);
        if (snapshotNumber is { } n)
            return snapshots.TryGetByNumber(pipelineId, n, out var s)
                ? new ConfigDocument(ConfigSnapshotInfo.From(s), s.Yaml)
                : new NotFoundProblem("Config snapshot #{0} of pipeline '{1}' not found.", n, pipelineId);
        return snapshots.TryGetLatest(pipelineId, out var latest)
            ? new ConfigDocument(ConfigSnapshotInfo.From(latest), latest.Yaml)
            : new ConfigDocument(null, "");
    }

    public Result<ConfigSnapshotInfo[]> History(Id<Pipeline> pipelineId)
        => pipelines.TryGet(pipelineId, out _)
            ? snapshots.ListByPipeline(pipelineId).Select(ConfigSnapshotInfo.From).ToArray()
            : new NotFoundProblem("Pipeline '{0}' not found.", pipelineId);

    public Task<Result<ConfigSaveResponse>> SaveAsync(Id<Pipeline> pipelineId, string yaml, string author, CancellationToken ct)
        => SaveCoreAsync(pipelineId, yaml, author, null, ct);

    /// <summary>Saves snapshot <paramref name="number"/>'s content again as the newest snapshot.</summary>
    public async Task<Result<ConfigSaveResponse>> RollbackAsync(Id<Pipeline> pipelineId, int number, string author, CancellationToken ct)
    {
        if (!snapshots.TryGetByNumber(pipelineId, number, out var old))
            return new NotFoundProblem("Config snapshot #{0} of pipeline '{1}' not found.", number, pipelineId);
        return await SaveCoreAsync(pipelineId, old.Yaml, author, number, ct);
    }

    /// <summary>The current document against <paramref name="versionNumber"/> (default: the pending version, else the active one).</summary>
    public async Task<Result<ConfigCheckResponse>> CheckAsync(Id<Pipeline> pipelineId, int? versionNumber, CancellationToken ct)
    {
        if (!pipelines.TryGet(pipelineId, out _))
            return new NotFoundProblem("Pipeline '{0}' not found.", pipelineId);

        PipelineVersion? version;
        if (versionNumber is { } n)
        {
            if (!versions.TryGetByNumber(pipelineId, n, out version))
                return new NotFoundProblem("Version {0} of pipeline '{1}' not found.", n, pipelineId);
        }
        else if (gate.TryGetPending(pipelineId, out var marker))
            versions.TryGet(marker!.VersionId, out version);
        else
            version = ActiveVersion(pipelineId);
        if (version is null)
            return new ConflictProblem("Pipeline '{0}' has no version yet.", pipelineId);

        var setSecrets = await secretNames.ListSetAsync(pipelineId, ct);
        return gate.Check(version, setSecrets).Map(ConfigCheckResponse.From);
    }

    private async Task<Result<ConfigSaveResponse>> SaveCoreAsync(Id<Pipeline> pipelineId, string yaml, string author, int? restoredFrom, CancellationToken ct)
    {
        if (!pipelines.TryGet(pipelineId, out _))
            return new NotFoundProblem("Pipeline '{0}' not found.", pipelineId);
        if (YamlJson.Parse(yaml).TryPickProblems(out var parseProblems, out var document))
            return Result.Failure<ConfigSaveResponse>(parseProblems);

        var setSecrets = await secretNames.ListSetAsync(pipelineId, ct);

        // Validity against the active version blocks the save; its secrets don't (they're the gate's business).
        if (ActiveVersion(pipelineId) is { } active && gate.Validate(active, document).TryPickProblems(out var invalid))
            return Result.Failure<ConfigSaveResponse>(invalid.Prepend("The document is not valid for the active version {0}", active.Number));

        if (snapshots.Add(pipelineId, yaml, author, restoredFrom).TryPickProblems(out var addProblems, out var snapshot))
            return Result.Failure<ConfigSaveResponse>(addProblems);
        logger.LogInformation("Saved config #{SnapshotNumber} of pipeline '{PipelineId}' by {Author}{RestoredFrom}",
            snapshot.Number, pipelineId, author, restoredFrom is { } r ? $" (rollback to #{r})" : "");

        // Checked before the re-check, so a version this save activates still shows its changed defaults.
        ConfigCheckResponse? pendingCheck = null;
        if (gate.TryGetPending(pipelineId, out var marker) && versions.TryGet(marker!.VersionId, out var pendingVersion)
            && pendingVersion.Id != ActiveVersion(pipelineId)?.Id && !gate.Check(pendingVersion, setSecrets).TryPickProblems(out _, out var pc))
            pendingCheck = ConfigCheckResponse.From(pc);

        if (gate.Recheck(pipelineId, setSecrets).TryPickProblems(out var recheckProblems, out var activated))
            logger.LogProblems(LogLevel.Warning, recheckProblems, "Could not re-check pending versions of pipeline '{PipelineId}'", pipelineId);
        else if (activated is not null)
            ReleaseHeldPush(services, pipelineId);

        ConfigCheckResponse? activeCheck = null;
        if (ActiveVersion(pipelineId) is { } nowActive && !gate.Check(nowActive, setSecrets).TryPickProblems(out _, out var c))
            activeCheck = ConfigCheckResponse.From(c);

        return new ConfigSaveResponse(ConfigSnapshotInfo.From(snapshot), activeCheck, pendingCheck, activated?.Number);
    }

    /// <summary>A push held behind the version that just activated starts now instead of on the next poll.</summary>
    internal static void ReleaseHeldPush(IServiceProvider services, Id<Pipeline> pipelineId)
        => services.GetService<PushTriggerService>()?.RequestCheck(pipelineId).DiscardResult();

    private PipelineVersion? ActiveVersion(Id<Pipeline> pipelineId)
        => activations.TryGet(pipelineId, out var activation)
           && versions.TryGet(activation switch
           {
               PipelineActivation.Active a => a.VersionId,
               PipelineActivation.Draining d => d.ToVersionId,
               _ => throw new InvalidOperationException($"Unknown activation state '{activation.GetType().Name}'."),
           }, out var version)
            ? version
            : null;
}
