using System.Text.Json.Serialization;
using Olve.Utilities.Ids;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.PipelineProcessing;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ScriptVerification), "script")]
public abstract record PipelineVerification(Id<PipelineVerification> Id, string Name, Id<PipelineProcessingStep> ProcessingId) : IHasId<Id<PipelineVerification>>;

public record ScriptVerification(
    Id<PipelineVerification> Id,
    string Name,
    Id<PipelineProcessingStep> ProcessingId,
    string Script) : PipelineVerification(Id, Name, ProcessingId);
