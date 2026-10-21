namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>Placeholder until push triggers start runs (B7).</summary>
public class PushTriggerHandler(ILogger<PushTriggerHandler> logger) : IPushTriggerHandler
{
    public Task<Result> FireAsync(PipelineConfigBinding binding, string headSha, CancellationToken ct)
    {
        logger.LogInformation("Push trigger of pipeline '{PipelineId}' for {Sha}: not implemented yet", binding.PipelineId, headSha);
        return Task.FromResult(Result.Success());
    }
}
