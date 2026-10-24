using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Configs;

/// <summary>
/// A version reconcile produced that the config document or secrets don't satisfy yet. The active
/// version stays active; this one activates on its own once satisfied. When <see cref="VersionId"/> is the
/// active version itself, the active version is <b>held</b> (a secret its config references was deleted):
/// new runs don't start until it is satisfied again.
/// </summary>
/// <param name="Reasons">Why, as of the last check (<c>config config.auth.clientId: is required</c>, <c>secret X not set</c>).</param>
/// <param name="ChangedDefaults">Defaults the version changes that the document relies on.</param>
public sealed record PendingVersion(
    Id<PipelineVersion> VersionId,
    int VersionNumber,
    DateTimeOffset Since,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<ChangedDefault> ChangedDefaults);

/// <summary>What the activation gate did with a version.</summary>
public abstract record ActivationOutcome
{
    /// <param name="ChangedDefaults">Defaults the activated version changes that the document relies on.</param>
    public sealed record Activated(PipelineActivation Activation, IReadOnlyList<ChangedDefault> ChangedDefaults) : ActivationOutcome;

    public sealed record Pending(PendingVersion Version) : ActivationOutcome;
}

/// <summary>
/// The activation gate: a version is switched to only when the current config document is valid for it and
/// every secret that config references is set. Callers fetch the set secret names first (K8s I/O) and pass
/// them in; null = unknown. Transient.
/// </summary>
public class ConfigActivationService(
    PipelineVersionService versions,
    PipelineActivationService activations,
    SwitchService switches,
    ConfigSnapshotService snapshots,
    AttachmentStore<Pipeline, PendingVersion> pending,
    RunPlanCache plans,
    RunSchedulingLock schedulingLock,
    TimeProvider timeProvider,
    ILogger<ConfigActivationService> logger)
{
    /// <summary>Checks the current config document against <paramref name="version"/> (changed defaults relative to the active version).</summary>
    public Result<ConfigCheck> Check(PipelineVersion version, IReadOnlySet<string>? setSecrets)
    {
        if (plans.Get(version.Id).TryPickProblems(out var problems, out var plan))
            return problems;
        Manifest? active = null;
        if (ActiveVersionId(version.PipelineId) is { } activeId && activeId != version.Id && plans.Get(activeId).TryPickProblems(out _, out var activePlan) is false)
            active = activePlan.Manifest;
        snapshots.TryGetLatest(version.PipelineId, out var latest);
        return ConfigValidation.Check(plan.Manifest, version.Number, snapshots.Document(latest), setSecrets, active);
    }

    /// <summary>Whether <paramref name="document"/> is valid for <paramref name="version"/> (secrets aside).</summary>
    public Result Validate(PipelineVersion version, System.Text.Json.Nodes.JsonObject document)
    {
        if (plans.Get(version.Id).TryPickProblems(out var problems, out var plan))
            return problems;
        return ConfigValidation.Effective(plan.Manifest, document).TryPickProblems(out var invalid)
            ? Result.Failure(invalid)
            : Result.Success();
    }

    /// <summary>Reconcile's switch: activates <paramref name="version"/> when satisfied, else marks it pending.</summary>
    public Result<ActivationOutcome> Activate(PipelineVersion version, IReadOnlySet<string>? setSecrets) => schedulingLock.Run<Result<ActivationOutcome>>(() =>
    {
        if (Check(version, setSecrets).TryPickProblems(out var problems, out var check))
            return problems;

        if (!check.Satisfied)
        {
            var marker = MarkPending(version, check);
            logger.LogInformation("Version {VersionNumber} of pipeline '{PipelineId}' is pending: {Reasons}",
                version.Number, version.PipelineId, string.Join("; ", marker.Reasons));
            return new ActivationOutcome.Pending(marker);
        }

        if (switches.Switch(version.PipelineId, version.Id).TryPickProblems(out var switchProblems, out var activation))
            return switchProblems;
        pending.Remove(version.PipelineId);
        return new ActivationOutcome.Activated(activation, check.ChangedDefaults);
    });

    /// <summary>An explicit switch (<c>pl pipeline switch</c>): refused with the reasons when not satisfied; clears any pending version.</summary>
    public Result<PipelineActivation> Switch(PipelineVersion version, bool cancelRunning, IReadOnlySet<string>? setSecrets) => schedulingLock.Run<Result<PipelineActivation>>(() =>
    {
        if (Check(version, setSecrets).TryPickProblems(out var problems, out var check))
            return problems;
        if (!check.Satisfied)
            return new ConflictProblem("Version {0} can't be activated: {1}.", version.Number, string.Join("; ", check.Reasons));

        if (switches.Switch(version.PipelineId, version.Id, cancelRunning).TryPickProblems(out var switchProblems, out var activation))
            return switchProblems;
        pending.Remove(version.PipelineId);
        return activation;
    });

    /// <summary>
    /// Re-checks after config or secrets changed: activates a pending version once satisfied (returns it),
    /// refreshes its reasons otherwise; holds the active version when its own config is no longer satisfied.
    /// </summary>
    public Result<PipelineVersion?> Recheck(Id<Pipeline> pipelineId, IReadOnlySet<string>? setSecrets) => schedulingLock.Run<Result<PipelineVersion?>>(() =>
    {
        var activeId = ActiveVersionId(pipelineId);
        var targetId = pending.TryGet(pipelineId, out var marker) ? marker.VersionId : activeId;
        if (targetId is null || !versions.TryGet(targetId.Value, out var target))
            return Result.Success<PipelineVersion?>(null);

        if (Check(target, setSecrets).TryPickProblems(out var problems, out var check))
            return Result.Failure<PipelineVersion?>(problems);

        if (!check.Satisfied)
        {
            MarkPending(target, check);
            return Result.Success<PipelineVersion?>(null);
        }

        if (marker is null)
            return Result.Success<PipelineVersion?>(null);

        if (target.Id != activeId && switches.Switch(pipelineId, target.Id).TryPickProblems(out var switchProblems))
            return Result.Failure<PipelineVersion?>(switchProblems);
        pending.Remove(pipelineId);
        logger.LogInformation("Version {VersionNumber} of pipeline '{PipelineId}' is satisfied by its config and secrets; {Action}",
            target.Number, pipelineId, target.Id == activeId ? "released" : "activated");
        return target;
    });

    /// <summary>The active (or pending) version whose current config references <paramref name="secret"/>, if any.</summary>
    public int? ReferencingVersion(Id<Pipeline> pipelineId, string secret, IReadOnlySet<string>? setSecrets)
    {
        var ids = new[] { ActiveVersionId(pipelineId), pending.TryGet(pipelineId, out var marker) ? marker.VersionId : null };
        foreach (var id in ids.OfType<Id<PipelineVersion>>().Distinct())
        {
            if (versions.TryGet(id, out var version) && !Check(version, setSecrets).TryPickProblems(out _, out var check)
                && check.ReferencedSecrets.Contains(secret, StringComparer.Ordinal))
                return version.Number;
        }
        return null;
    }

    /// <summary>A version reconcile switched away from (or matched the active one): nothing is pending any more.</summary>
    public void ClearPending(Id<Pipeline> pipelineId) => pending.Remove(pipelineId);

    public bool TryGetPending(Id<Pipeline> pipelineId, out PendingVersion? marker)
        => pending.TryGet(pipelineId, out marker);

    public PendingVersionInfo? PendingInfo(Id<Pipeline> pipelineId)
        => pending.TryGet(pipelineId, out var marker) ? PendingVersionInfo.From(marker, marker.VersionId == ActiveVersionId(pipelineId)) : null;

    /// <summary>The active version is held: new runs wait until its config is satisfied again.</summary>
    public PendingVersion? Held(Id<Pipeline> pipelineId)
        => pending.TryGet(pipelineId, out var marker) && marker.VersionId == ActiveVersionId(pipelineId) ? marker : null;

    private PendingVersion MarkPending(PipelineVersion version, ConfigCheck check)
    {
        var since = pending.TryGet(version.PipelineId, out var existing) && existing.VersionId == version.Id
            ? existing.Since
            : timeProvider.GetUtcNow();
        var marker = new PendingVersion(version.Id, version.Number, since, check.Reasons, check.ChangedDefaults);
        if (existing is null || existing.VersionId != marker.VersionId || !existing.Reasons.SequenceEqual(marker.Reasons)
            || !existing.ChangedDefaults.SequenceEqual(marker.ChangedDefaults))
            pending.Set(version.PipelineId, marker);
        return marker;
    }

    private Id<PipelineVersion>? ActiveVersionId(Id<Pipeline> pipelineId)
        => activations.TryGet(pipelineId, out var activation)
            ? activation switch
            {
                PipelineActivation.Active a => a.VersionId,
                PipelineActivation.Draining d => d.ToVersionId,
                _ => null,
            }
            : null;
}
