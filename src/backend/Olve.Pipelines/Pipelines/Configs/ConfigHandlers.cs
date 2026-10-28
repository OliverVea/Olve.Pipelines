using Olve.Pipelines.Kubernetes.Api;
using Olve.Pipelines.Shared.Problems;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.Pipelines.Configs;

public sealed class GetConfigHandler(ConfigService config) : Contract.IConfigGetHandler
{
    public Task<Contract.ConfigGetResponse> HandleAsync(Contract.ConfigGetRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<Contract.ConfigGetResponse>(config.Get(PipelineViews.PipelineId(request.PipelineId), request.Snapshot).TryPickProblems(out var problems, out var document)
            ? new Contract.ConfigGetResponse.NotFound(ApiProblems.Of([.. problems]))
            : ConfigViews.ToApi(document));
}

/// <summary>Writes need the same authorization as setting a secret.</summary>
public sealed class SetConfigHandler(ConfigService config, IHttpContextAccessor http) : Contract.IConfigSetHandler
{
    public async Task<Contract.ConfigSetResponse> HandleAsync(Contract.ConfigSetRequest request, CancellationToken cancellationToken)
    {
        var result = await config.SaveAsync(PipelineViews.PipelineId(request.PipelineId), request.Body.Yaml, SecretViews.Actor(http.HttpContext?.User), cancellationToken);
        if (result.TryPickProblems(out var problems, out var saved))
        {
            var problem = ApiProblems.Of([.. problems]);
            return problem.Status switch
            {
                StatusCodes.Status404NotFound => new Contract.ConfigSetResponse.NotFound(problem),
                StatusCodes.Status409Conflict => new Contract.ConfigSetResponse.Conflict(problem),
                _ => new Contract.ConfigSetResponse.BadRequest(problem),
            };
        }

        return ConfigViews.ToApi(saved);
    }
}

public sealed class ListConfigSnapshotsHandler(ConfigService config) : Contract.IConfigHistoryHandler
{
    public Task<Contract.ConfigHistoryResponse> HandleAsync(Contract.ConfigHistoryRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<Contract.ConfigHistoryResponse>(config.History(PipelineViews.PipelineId(request.PipelineId)).TryPickProblems(out var problems, out var history)
            ? new Contract.ConfigHistoryResponse.NotFound(ApiProblems.Of([.. problems]))
            : new Contract.ConfigHistoryResponse.Ok([.. history.Select(ConfigViews.ToApi)]));
}

public sealed class RollbackConfigHandler(ConfigService config, IHttpContextAccessor http) : Contract.IConfigRollbackHandler
{
    public async Task<Contract.ConfigRollbackResponse> HandleAsync(Contract.ConfigRollbackRequest request, CancellationToken cancellationToken)
    {
        var result = await config.RollbackAsync(PipelineViews.PipelineId(request.PipelineId), request.Body.Snapshot, SecretViews.Actor(http.HttpContext?.User), cancellationToken);
        if (result.TryPickProblems(out var problems, out var saved))
        {
            var problem = ApiProblems.Of([.. problems]);
            return problem.Status switch
            {
                StatusCodes.Status404NotFound => new Contract.ConfigRollbackResponse.NotFound(problem),
                StatusCodes.Status409Conflict => new Contract.ConfigRollbackResponse.Conflict(problem),
                _ => new Contract.ConfigRollbackResponse.BadRequest(problem),
            };
        }

        return ConfigViews.ToApi(saved);
    }
}

public sealed class CheckConfigHandler(ConfigService config) : Contract.IConfigCheckHandler
{
    public async Task<Contract.ConfigCheckResponse> HandleAsync(Contract.ConfigCheckRequest request, CancellationToken cancellationToken)
    {
        var result = await config.CheckAsync(PipelineViews.PipelineId(request.PipelineId), request.Version, cancellationToken);
        if (result.TryPickProblems(out var problems, out var check))
        {
            var problem = ApiProblems.Of([.. problems]);
            return problem.Status switch
            {
                StatusCodes.Status404NotFound => new Contract.ConfigCheckResponse.NotFound(problem),
                _ => new Contract.ConfigCheckResponse.BadRequest(problem),
            };
        }

        return ConfigViews.ToApi(check);
    }
}

public static class ConfigViews
{
    public static Contract.ConfigSnapshotSummary ToApi(ConfigSnapshotInfo s) => new()
    {
        Number = s.Number,
        Author = s.Author,
        CreatedAt = s.CreatedAt,
        RestoredFrom = s.RestoredFrom,
    };

    public static Contract.ConfigDocument ToApi(ConfigDocument d) => new()
    {
        Snapshot = d.Snapshot is null ? null : ToApi(d.Snapshot),
        Yaml = d.Yaml,
    };

    public static Contract.ConfigCheck ToApi(ConfigCheckResponse c) => new()
    {
        Version = c.Version,
        Valid = c.Valid,
        Satisfied = c.Satisfied,
        Problems = c.Problems,
        ReferencedSecrets = c.ReferencedSecrets,
        MissingSecrets = c.MissingSecrets,
        ChangedDefaults = [.. c.ChangedDefaults.Select(d => new Contract.ChangedDefault { Path = d.Path, From = d.From, To = d.To })],
        Effective = c.Effective,
        Unmet = c.Unmet,
    };

    public static Contract.ConfigSaveResult ToApi(ConfigSaveResponse s) => new()
    {
        Snapshot = ToApi(s.Snapshot),
        Active = s.Active is null ? null : ToApi(s.Active),
        Pending = s.Pending is null ? null : ToApi(s.Pending),
        ActivatedVersion = s.ActivatedVersion,
    };
}
