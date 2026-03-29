using System.Text.Json.Serialization;
using Olve.Pipelines.Pipelines;
using Olve.Utilities.Ids;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.PipelineProcessing;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ScriptProcessingStep), "script")]
public abstract record PipelineProcessingStep(Id<PipelineProcessingStep> Id, string Name, Id<Pipeline> PipelineId) : IHasId<Id<PipelineProcessingStep>>;

public record ScriptProcessingStep(
    Id<PipelineProcessingStep> Id,
    string Name,
    Id<Pipeline> PipelineId,
    string Script) : PipelineProcessingStep(Id, Name, PipelineId);
