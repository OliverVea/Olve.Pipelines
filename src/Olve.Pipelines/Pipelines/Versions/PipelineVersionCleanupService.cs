using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Versions;

/// <summary>Deletes a pipeline's versions when the pipeline is deleted. (Activation state is an
/// attachment and goes with the pipeline automatically.) Binaries/manifests in Garage are left to
/// retention (B9).</summary>
public class PipelineVersionCleanupService(PipelineVersionService versions, ILogger<PipelineVersionCleanupService> logger)
{
    public void HandlePipelineDeleted(Id<Pipeline> pipelineId)
    {
        foreach (var version in versions.ListByPipeline(pipelineId))
        {
            var result = versions.Delete(version.Id);
            if (result.IsError && result.TryPickProblems(out var problems))
                logger.LogProblems(LogLevel.Warning, problems,
                    "Failed to delete version {VersionNumber} ('{VersionId}') of deleted pipeline '{PipelineId}'",
                    version.Number, version.Id, pipelineId);
        }
    }
}
