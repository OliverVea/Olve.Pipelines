using Olve.Pipelines.Pipelines;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.FailureHandlers;

/// <summary>Which phase the failed step belonged to. Surfaced to handlers as <c>PIPELINE_FAILED_STEP_KIND</c>.</summary>
public enum FailedStepKind
{
    Production,
    Processing,
}

/// <summary>
/// The failure-context half of a failure handler's environment (class (a) in the env contract): the
/// well-known <c>PIPELINE_*</c> variables the orchestrator knows at failure time and a handler script
/// cannot compute for itself. This is the reusability boundary — one handler script serves every
/// pipeline precisely because it reads these from the environment rather than baking any in.
/// </summary>
public record FailureContext(
    Id<Pipeline> PipelineId,
    string PipelineName,
    string FailedStepName,
    FailedStepKind FailedStepKind,
    string FailureReason,
    string LogKey)
{
    // Well-known names, GitHub-Actions style. These are a public contract: handler scripts depend on
    // the exact spelling, so treat renames as breaking changes.
    public const string PipelineIdKey = "PIPELINE_ID";
    public const string PipelineNameKey = "PIPELINE_NAME";
    public const string FailedStepKey = "PIPELINE_FAILED_STEP";
    public const string FailedStepKindKey = "PIPELINE_FAILED_STEP_KIND";
    public const string FailureReasonKey = "PIPELINE_FAILURE_REASON";
    public const string LogKeyKey = "PIPELINE_LOG_KEY";

    /// <summary>
    /// Renders the failure context as environment variables. The pipeline id uses the dashed-GUID form
    /// the API exposes (not the <c>:N</c> form used for K8s resource names) so a handler can reference
    /// the pipeline back through the API.
    /// </summary>
    public Dictionary<string, string> ToEnvironmentVariables() => new()
    {
        [PipelineIdKey] = PipelineId.Value.Value.ToString(),
        [PipelineNameKey] = PipelineName,
        [FailedStepKey] = FailedStepName,
        [FailedStepKindKey] = FailedStepKind == FailedStepKind.Production ? "production" : "processing",
        [FailureReasonKey] = FailureReason,
        [LogKeyKey] = LogKey,
    };
}
