using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines.Configs;
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
