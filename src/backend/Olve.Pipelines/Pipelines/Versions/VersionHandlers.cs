using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Pipelines.Configs;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Shared.Problems;
using Olve.TypeSpec;
using Api = Olve.Pipelines.Api;

namespace Olve.Pipelines.Pipelines.Versions;

[OperationHandler]
public sealed class ListVersionsHandler(PipelineService pipelines, PipelineVersionService versions, PipelineActivationService activations) : Api.IVersionsListHandler
{
    public Task<Api.VersionsListResponse> HandleAsync(Api.VersionsListRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        if (!pipelines.TryGet(id, out _))
            return Task.FromResult<Api.VersionsListResponse>(new Api.VersionsListResponse.NotFound(PipelineViews.NotFound(id)));
        return Task.FromResult<Api.VersionsListResponse>(new Api.PipelineVersions
        {
            Activation = PipelineViews.ToApi(activations.TryGet(id, out var activation) ? activation : null),
            Versions = [.. versions.ListByPipeline(id).Select(VersionViews.Summary)],
        });
    }
}

[OperationHandler]
public sealed class GetVersionHandler(PipelineVersionService versions) : Api.IVersionsGetHandler
{
    public Task<Api.VersionsGetResponse> HandleAsync(Api.VersionsGetRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        return Task.FromResult<Api.VersionsGetResponse>(versions.TryGetByNumber(id, request.Number, out var version)
            ? VersionViews.ToApi(version)
            : new Api.VersionsGetResponse.NotFound(VersionViews.NotFound(id, request.Number)));
    }
}

/// <summary>The version with its selects resolved by a config snapshot (default the latest): what new runs execute.</summary>
[OperationHandler]
public sealed class GetEffectivePipelineHandler(PipelineVersionService versions, RunPlanCache plans, ConfigSnapshotService snapshots) : Api.IVersionsEffectiveHandler
{
    public Task<Api.VersionsEffectiveResponse> HandleAsync(Api.VersionsEffectiveRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        if (!versions.TryGetByNumber(id, request.Number, out var version))
            return Task.FromResult<Api.VersionsEffectiveResponse>(new Api.VersionsEffectiveResponse.NotFound(VersionViews.NotFound(id, request.Number)));
        if (plans.ForSnapshot(version.Id, request.Snapshot, snapshots).TryPickProblems(out var problems, out var plan))
        {
            var problem = ApiProblems.Of([.. problems]);
            return Task.FromResult<Api.VersionsEffectiveResponse>(problem.Status switch
            {
                StatusCodes.Status404NotFound => new Api.VersionsEffectiveResponse.NotFound(problem),
                StatusCodes.Status409Conflict => new Api.VersionsEffectiveResponse.Conflict(problem),
                _ => new Api.VersionsEffectiveResponse.BadRequest(problem),
            });
        }

        int? used = request.Snapshot ?? (snapshots.TryGetLatest(id, out var latest) ? latest.Number : null);
        var cases = plan.Stages.SelectMany(s => s.Steps.Select(step => (step.Name, s.Cases)))
            .Where(x => x.Cases.Count > 0)
            .GroupBy(x => x.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.First().Cases], StringComparer.Ordinal);
        return Task.FromResult<Api.VersionsEffectiveResponse>(new Api.EffectivePipeline
        {
            Version = request.Number,
            ConfigSnapshot = used,
            Selected = plan.Selected,
            Manifest = ManifestWriter.ToCanonicalJson(plan.Manifest with { Stages = RunPlan.Effective(plan.Manifest.Stages, plan.Selected) }),
            Cases = cases,
        });
    }
}

/// <summary>Switch (or roll back) the active version: Active(a) → Draining(a → b) → Active(b). Refused with the reasons when the config or a secret doesn't satisfy the version.</summary>
[OperationHandler]
public sealed class SwitchVersionHandler(PipelineVersionService versions, ConfigActivationService gate, IPipelineSecretNames secretNames) : Api.IVersionsSwitchHandler
{
    public async Task<Api.VersionsSwitchResponse> HandleAsync(Api.VersionsSwitchRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        if (!versions.TryGetByNumber(id, request.Body.Version, out var version))
            return new Api.VersionsSwitchResponse.NotFound(VersionViews.NotFound(id, request.Body.Version));
        var result = gate.Switch(version, request.Body.CancelRunning ?? false, await secretNames.ListSetAsync(id, cancellationToken));
        if (result.TryPickProblems(out var problems, out var activation))
        {
            var problem = ApiProblems.Of([.. problems]);
            return problem.Status switch
            {
                StatusCodes.Status404NotFound => new Api.VersionsSwitchResponse.NotFound(problem),
                StatusCodes.Status409Conflict => new Api.VersionsSwitchResponse.Conflict(problem),
                _ => new Api.VersionsSwitchResponse.BadRequest(problem),
            };
        }

        return new Api.VersionsSwitchResponse.Ok(PipelineViews.ToApi(activation)!);
    }
}

public static class VersionViews
{
    public static ApiProblem NotFound(Id<Pipeline> pipelineId, int number) =>
        ApiProblems.Of(new NotFoundProblem("Version {0} of pipeline '{1}' not found.", number, pipelineId));

    public static Api.PipelineVersionSummary Summary(PipelineVersion v) => new()
    {
        Id = v.Id.Value.Value,
        Number = v.Number,
        ManifestHash = v.ManifestHash,
        CommitSha = v.CommitSha,
        StepVersions = v.StepVersions,
        ModelVersions = v.ModelVersions,
        CreatedAt = v.CreatedAt,
    };

    public static Api.PipelineVersion ToApi(PipelineVersion v) => new()
    {
        Id = v.Id.Value.Value,
        PipelineId = v.PipelineId.Value.Value,
        Number = v.Number,
        Manifest = v.Manifest,
        ManifestHash = v.ManifestHash,
        CommitSha = v.CommitSha,
        StepVersions = v.StepVersions,
        ModelVersions = v.ModelVersions,
        CreatedAt = v.CreatedAt,
    };
}
