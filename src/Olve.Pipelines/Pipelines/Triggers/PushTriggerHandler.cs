using System.Text.Json;
using System.Text.Json.Nodes;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Sync;

namespace Olve.Pipelines.Pipelines.Triggers;

/// <summary>
/// A bound branch's head advanced (binding webhook or deploy poll): build the GitHub-push-shaped event
/// <c>{ref, after, repository: {full_name}}</c> and run the pipeline's <c>push</c> trigger. Pipelines
/// without an <c>onPush</c> ignore pushes.
/// </summary>
public class PushTriggerHandler(
    RunInputService inputs,
    TriggerMappingService mapping,
    ILogger<PushTriggerHandler> logger) : IPushTriggerHandler
{
    public static string PushEvent(string repo, string branch, string sha) => new JsonObject
    {
        ["ref"] = $"refs/heads/{branch}",
        ["after"] = sha,
        ["repository"] = new JsonObject { ["full_name"] = repo },
    }.ToJsonString();

    public async Task<Result> FireAsync(PipelineConfigBinding binding, string headSha, CancellationToken ct, RunOrigin? origin = null)
    {
        if (inputs.GetTarget(binding.PipelineId).TryPickProblems(out var problems, out var target))
            return Result.Failure(problems);

        if (target.Manifest.Triggers.OfType<PushTrigger>().FirstOrDefault() is null)
        {
            logger.LogInformation("Pipeline '{PipelineId}' (version {VersionNumber}) has no push trigger; ignoring {Sha}",
                binding.PipelineId, target.Version.Number, headSha);
            return Result.Success();
        }

        var outcome = await mapping.MapAndStartAsync(binding.PipelineId, PushTrigger.PushName, RunTriggerKind.Push,
            PushEvent(binding.Repo, binding.Branch, headSha), ct, origin);
        return outcome.TryPickProblems(out var startProblems) ? Result.Failure(startProblems) : Result.Success();
    }
}
