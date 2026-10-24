using Olve.Pipelines.Configuration;
using Olve.Pipelines.Kubernetes.Api;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Configs;

/// <summary>A pipeline's config document: read, save (a new snapshot), history, rollback, check against a version.</summary>
public static class ConfigEndpoints
{
    public static void MapConfigEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pipelines/{pipelineId}/config");

        group.MapGet("/", Result<ConfigDocument> (ConfigService config, Id<Pipeline> pipelineId, int? snapshot)
                => config.Get(pipelineId, snapshot))
            .WithApiResultMapping<ConfigDocument>()
            .WithName("GetPipelineConfig")
            .WithTags("Config")
            .WithSummary("Get the config document")
            .WithDescription("The YAML document as saved, and its snapshot (number, author, time, `restoredFrom`). `snapshot` gets an older one; `yaml` is empty before the first save.")
            .RequireRead();

        // Writes need the same authorization as setting a secret.
        group.MapPut("/", Task<Result<ConfigSaveResponse>> (ConfigService config, HttpContext http, Id<Pipeline> pipelineId, SetConfigRequest request, CancellationToken ct)
                => config.SaveAsync(pipelineId, request.Yaml, SecretEndpoints.Actor(http.User), ct))
            .WithApiResultMapping<ConfigSaveResponse>()
            .WithName("SetPipelineConfig")
            .WithTags("Config")
            .WithSummary("Save the config document")
            .WithDescription("Saves `yaml` as the next snapshot. 400 with each problem when the active version rejects it. The answer has the check against the active and the pending version, and the version it activated, if any.");

        group.MapGet("/history", Result<ConfigSnapshotInfo[]> (ConfigService config, Id<Pipeline> pipelineId)
                => config.History(pipelineId))
            .WithApiResultMapping<ConfigSnapshotInfo[]>()
            .WithName("ListPipelineConfigSnapshots")
            .WithTags("Config")
            .WithSummary("List config snapshots")
            .WithDescription("Newest first.")
            .RequireRead();

        group.MapPost("/rollback", Task<Result<ConfigSaveResponse>> (ConfigService config, HttpContext http, Id<Pipeline> pipelineId, RollbackConfigRequest request, CancellationToken ct)
                => config.RollbackAsync(pipelineId, request.Snapshot, SecretEndpoints.Actor(http.User), ct))
            .WithApiResultMapping<ConfigSaveResponse>()
            .WithName("RollbackPipelineConfig")
            .WithTags("Config")
            .WithSummary("Roll back the config")
            .WithDescription("Saves snapshot `snapshot`'s content again as the newest.");

        group.MapGet("/check", Task<Result<ConfigCheckResponse>> (ConfigService config, Id<Pipeline> pipelineId, int? version, CancellationToken ct)
                => config.CheckAsync(pipelineId, version, ct))
            .WithApiResultMapping<ConfigCheckResponse>()
            .WithName("CheckPipelineConfig")
            .WithTags("Config")
            .WithSummary("Check the config")
            .WithDescription("The current document against `version` (default: the pending version, else the active one): validity, whether it is satisfied, problems, referenced and missing secrets, changed defaults and the effective values.")
            .RequireRead();
    }
}

/// <summary>A deleted pipeline's config snapshots go with it.</summary>
public class ConfigEventRegistration(PipelineEvents pipelineEvents, IServiceProvider sp) : IRunOnStartup
{
    public Result Run()
    {
        pipelineEvents.OnDeleted.Subscribe(e => sp.GetRequiredService<ConfigSnapshotService>().DeleteAll(e.Id));
        return Result.Success();
    }
}
