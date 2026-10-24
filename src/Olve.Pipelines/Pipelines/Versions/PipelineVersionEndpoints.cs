using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines.Configs;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Scheduling;

namespace Olve.Pipelines.Pipelines.Versions;

/// <summary>A version without its manifest.</summary>
public record PipelineVersionSummary(
    Id<PipelineVersion> Id,
    int Number,
    string ManifestHash,
    string? CommitSha,
    IReadOnlyDictionary<string, int> StepVersions,
    IReadOnlyDictionary<string, int> ModelVersions,
    DateTimeOffset CreatedAt)
{
    public static PipelineVersionSummary From(PipelineVersion v)
        => new(v.Id, v.Number, v.ManifestHash, v.CommitSha, v.StepVersions, v.ModelVersions, v.CreatedAt);
}

/// <summary>The pipeline's activation state (null = no version yet) and its versions, newest first.</summary>
public record PipelineVersionsResponse(PipelineActivation? Activation, PipelineVersionSummary[] Versions);

public record SwitchVersionRequest(int Version, bool CancelRunning = false);

/// <summary>
/// A version as the pipeline's current config makes it run: each <c>select</c> replaced by the case the config picks.
/// </summary>
/// <param name="ConfigSnapshot">The config snapshot it was resolved with (null = the pipeline has no config yet).</param>
/// <param name="Selected">Each select's reference → the case picked.</param>
/// <param name="Manifest">The effective manifest (canonical JSON): the version's, with selects resolved.</param>
/// <param name="Cases">Step name → the select cases it was picked by (<c>target.beta.kind=inCluster</c>); steps outside selects are absent.</param>
public record EffectivePipelineResponse(int Version, int? ConfigSnapshot, IReadOnlyDictionary<string, string> Selected, string Manifest, IReadOnlyDictionary<string, string[]> Cases);

/// <summary>Minimal versions/switch endpoints for M1 (spec §6). The full API is B8.</summary>
public static class PipelineVersionEndpoints
{
    public static void MapPipelineVersionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pipelines/{pipelineId}/versions");

        group.MapGet("/", Result<PipelineVersionsResponse> (
                PipelineVersionService versions, PipelineActivationService activations, Id<Pipeline> pipelineId)
                => new PipelineVersionsResponse(
                    activations.TryGet(pipelineId, out var activation) ? activation : null,
                    versions.ListByPipeline(pipelineId).Select(PipelineVersionSummary.From).ToArray()))
            .WithApiResultMapping<PipelineVersionsResponse>()
            .WithName("ListPipelineVersions")
            .RequireRead();

        // The version including its canonical manifest (JSON text, as described by the build Job).
        group.MapGet("/{number:int}", Result<PipelineVersion> (PipelineVersionService versions, Id<Pipeline> pipelineId, int number)
                => versions.TryGetByNumber(pipelineId, number, out var version)
                    ? version
                    : new NotFoundProblem("Version {0} of pipeline '{1}' not found.", number, pipelineId))
            .WithApiResultMapping<PipelineVersion>()
            .WithName("GetPipelineVersion")
            .RequireRead();

        // The version with its selects resolved by the current config: what new runs execute.
        // ?snapshot=N resolves with that config snapshot (a run's pinned one); default: the latest.
        group.MapGet("/{number:int}/effective", Result<EffectivePipelineResponse> (
                PipelineVersionService versions, RunPlanCache plans, ConfigSnapshotService snapshots, Id<Pipeline> pipelineId, int number, int? snapshot) =>
            {
                if (!versions.TryGetByNumber(pipelineId, number, out var version))
                    return new NotFoundProblem("Version {0} of pipeline '{1}' not found.", number, pipelineId);
                if (plans.ForSnapshot(version.Id, snapshot, snapshots).TryPickProblems(out var problems, out var plan))
                    return problems;
                int? used = snapshot ?? (snapshots.TryGetLatest(pipelineId, out var latest) ? latest.Number : null);
                var cases = plan.Stages.SelectMany(s => s.Steps.Select(step => (step.Name, s.Cases)))
                    .Where(x => x.Cases.Count > 0)
                    .GroupBy(x => x.Name, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First().Cases.ToArray(), StringComparer.Ordinal);
                return new EffectivePipelineResponse(number, used, plan.Selected,
                    ManifestWriter.ToCanonicalJson(plan.Manifest with { Stages = RunPlan.Effective(plan.Manifest.Stages, plan.Selected) }), cases);
            })
            .WithApiResultMapping<EffectivePipelineResponse>()
            .WithName("GetEffectivePipeline")
            .RequireRead();

        // Switch (or roll back) the active version: Active(a) → Draining(a → b) → Active(b). Refused (409, with
        // the reasons) when the config document or a secret it references doesn't satisfy the version.
        app.MapPost("/api/pipelines/{pipelineId}/switch", async Task<Result<PipelineActivation>> (
                PipelineVersionService versions, ConfigActivationService gate, IPipelineSecretNames secretNames,
                Id<Pipeline> pipelineId, SwitchVersionRequest request, CancellationToken ct) =>
            {
                if (!versions.TryGetByNumber(pipelineId, request.Version, out var version))
                    return new NotFoundProblem("Version {0} of pipeline '{1}' not found.", request.Version, pipelineId);
                return gate.Switch(version, request.CancelRunning, await secretNames.ListSetAsync(pipelineId, ct));
            })
            .WithApiResultMapping<PipelineActivation>()
            .WithName("SwitchPipelineVersion");
    }
}
