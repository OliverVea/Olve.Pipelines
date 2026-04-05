using System.Text.Json.Serialization;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Pipelines.Triggers;

public record Trigger(
    Id<Trigger> Id,
    Id<Pipeline> PipelineId,
    string Name,
    TriggerTarget Target,
    string Secret,
    DateTimeOffset CreatedAt) : IHasId<Id<Trigger>>;

[JsonDerivedType(typeof(ProductionTriggerTarget), "production")]
[JsonDerivedType(typeof(ProcessingTriggerTarget), "processing")]
public abstract record TriggerTarget;

public record ProductionTriggerTarget : TriggerTarget;

public record ProcessingTriggerTarget(Id<ProcessingStep> ProcessingStepId) : TriggerTarget;
