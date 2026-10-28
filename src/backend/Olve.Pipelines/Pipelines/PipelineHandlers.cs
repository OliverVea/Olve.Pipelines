using Olve.Pipelines.Pipelines.Configs;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Shared.Problems;
using Api = Olve.Pipelines.Api;

namespace Olve.Pipelines.Pipelines;

public sealed class ListPipelinesHandler(PipelineService pipelines) : Api.IPipelinesListHandler
{
    public Task<Api.PipelinesListResponse> HandleAsync(Api.PipelinesListRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<Api.PipelinesListResponse>(new Api.PipelinesListResponse.Ok([.. pipelines.List().Select(PipelineViews.ToApi)]));
}

public sealed class ListPipelineSummariesHandler(PipelineSummaryService summaries) : Api.IPipelinesSummariesHandler
{
    public Task<Api.PipelinesSummariesResponse> HandleAsync(Api.PipelinesSummariesRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<Api.PipelinesSummariesResponse>(new Api.PipelinesSummariesResponse.Ok([.. summaries.List().Select(PipelineViews.ToApi)]));
}

public sealed class GetPipelineHandler(PipelineService pipelines) : Api.IPipelinesGetHandler
{
    public Task<Api.PipelinesGetResponse> HandleAsync(Api.PipelinesGetRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        return Task.FromResult<Api.PipelinesGetResponse>(pipelines.TryGet(id, out var pipeline)
            ? PipelineViews.ToApi(pipeline)
            : new Api.PipelinesGetResponse.NotFound(PipelineViews.NotFound(id)));
    }
}

/// <summary>Deletes the pipeline with its runs, versions, binding and secrets (the cleanup services cascade).</summary>
public sealed class DeletePipelineHandler(PipelineService pipelines) : Api.IPipelinesDeleteHandler
{
    public Task<Api.PipelinesDeleteResponse> HandleAsync(Api.PipelinesDeleteRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        return Task.FromResult<Api.PipelinesDeleteResponse>(pipelines.Delete(id).Succeeded
            ? new Api.PipelinesDeleteResponse.Ok()
            : new Api.PipelinesDeleteResponse.NotFound(PipelineViews.NotFound(id)));
    }
}

public sealed class GetPipelineHealthHandler(PipelineService pipelines, PipelineHealthService health) : Api.IPipelinesHealthHandler
{
    public Task<Api.PipelinesHealthResponse> HandleAsync(Api.PipelinesHealthRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        return Task.FromResult<Api.PipelinesHealthResponse>(pipelines.TryGet(id, out _)
            ? PipelineViews.ToApi(health.Get(id))
            : new Api.PipelinesHealthResponse.NotFound(PipelineViews.NotFound(id)));
    }
}

/// <summary>The contract's view of the pipeline types.</summary>
public static class PipelineViews
{
    public static Id<Pipeline> PipelineId(Guid id) => new(new Id(id));

    public static ApiProblem NotFound(Id<Pipeline> id) => ApiProblems.Of(new NotFoundProblem("Pipeline '{0}' not found.", id));

    public static Api.Pipeline ToApi(Pipeline p) => new() { Id = p.Id.Value.Value, Name = p.Name, Description = p.Description };

    public static Api.PipelineSummary ToApi(PipelineSummary s) => new()
    {
        Id = s.Id.Value.Value,
        Name = s.Name,
        Description = s.Description,
        Repo = s.Repo,
        Health = ToApi(s.Health),
        Stages = [.. s.Stages.Select(ToApi)],
    };

    public static Api.PipelineHealth ToApi(PipelineHealth h) => new()
    {
        Status = Status(h.Status),
        LastRun = h.LastRun,
        LastSettledRun = h.LastSettledRun,
        RunningRuns = h.RunningRuns,
        Activation = ToApi(h.Activation),
        Draining = h.Draining,
        BlockedSteps = h.BlockedSteps,
        HeldRuns = h.HeldRuns,
        Pending = h.Pending is null ? null : ToApi(h.Pending),
    };

    public static Api.PipelineActivation? ToApi(PipelineActivation? activation) => activation switch
    {
        PipelineActivation.Active a => new Api.PipelineActivationActive
        {
            VersionId = a.VersionId.Value.Value,
            VersionNumber = a.VersionNumber,
            Since = a.Since,
        },
        PipelineActivation.Draining d => new Api.PipelineActivationDraining
        {
            FromVersionId = d.FromVersionId.Value.Value,
            FromVersionNumber = d.FromVersionNumber,
            ToVersionId = d.ToVersionId.Value.Value,
            ToVersionNumber = d.ToVersionNumber,
            CancelRunning = d.CancelRunning,
            Since = d.Since,
        },
        _ => null,
    };

    public static Api.PendingVersionInfo ToApi(PendingVersionInfo p) => new()
    {
        Version = p.Version,
        Since = p.Since,
        Held = p.Held,
        Reasons = p.Reasons,
        ChangedDefaults = [.. p.ChangedDefaults.Select(c => new Api.ChangedDefault { Path = c.Path, From = c.From, To = c.To })],
    };

    private static Api.PipelineStageSummary ToApi(PipelineStageSummary s) => new()
    {
        When = [.. s.When.Select(c => new Api.RunStageCondition { Field = c.Field, Values = c.Values })],
        Steps = [.. s.Steps.Select(st => new Api.PipelineStepSummary { Name = st.Name, Status = st.Status, RunNumber = st.RunNumber, At = st.At })],
    };

    private static Api.PipelineStatus Status(string status) => status switch
    {
        PipelineStatus.Healthy => Api.PipelineStatus.Healthy,
        PipelineStatus.RunningHealthy => Api.PipelineStatus.RunningHealthy,
        PipelineStatus.RunningUnhealthy => Api.PipelineStatus.RunningUnhealthy,
        PipelineStatus.Unhealthy => Api.PipelineStatus.Unhealthy,
        _ => Api.PipelineStatus.Idle,
    };
}
