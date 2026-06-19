using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Olve.Pipelines.Pipelines.Sync;

[Description("Beta: shape may change within the 0.x major version.")]
public record PipelineDocument(
    string ApiVersion,
    string Name,
    IReadOnlyList<ProductionStepDocument> ProductionSteps,
    IReadOnlyList<ProcessingStepDocument> ProcessingSteps,
    IReadOnlyList<TriggerDocument> Triggers);

public record ProductionStepDocument(
    string Name,
    StepConfigurationDocument? Configuration);

public record ProcessingStepDocument(
    string Name,
    StepConfigurationDocument? Configuration);

public record StepConfigurationDocument(
    string Image,
    string Script,
    IReadOnlyDictionary<string, string>? EnvironmentVariables);

/// <summary>
/// A failure-handler binding in <c>.pipelines/config.yaml</c>: a library handler by name, the steps it
/// reacts to (empty/omitted = whole pipeline), and the handler-config env. Lives on
/// <see cref="PipelineManifest"/> (like <c>secrets:</c>) rather than <see cref="PipelineDocument"/>.
/// </summary>
public record FailureHandlerDocument(
    string Handler,
    IReadOnlyList<string>? Steps,
    IReadOnlyDictionary<string, string>? Env);

public record TriggerDocument(
    string Name,
    TriggerTargetDocument Target);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ProductionTargetDocument), "production")]
[JsonDerivedType(typeof(ProcessingTargetDocument), "processing")]
[JsonDerivedType(typeof(PollTargetDocument), "poll")]
[JsonDerivedType(typeof(GitHubTargetDocument), "github")]
public abstract record TriggerTargetDocument;

public record ProductionTargetDocument : TriggerTargetDocument;

public record ProcessingTargetDocument(string ProcessingStepName) : TriggerTargetDocument;

public record PollTargetDocument(
    string Url,
    IReadOnlyDictionary<string, string>? Headers,
    string ValuePath,
    int IntervalSeconds = 60) : TriggerTargetDocument;

public record GitHubTargetDocument(
    string Owner,
    string Repo,
    string Branch,
    string TokenSecret) : TriggerTargetDocument;
