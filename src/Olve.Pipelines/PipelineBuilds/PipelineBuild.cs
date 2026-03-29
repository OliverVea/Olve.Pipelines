using System.Text.Json.Serialization;
using Olve.Pipelines.Pipelines;
using Olve.Utilities.Ids;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.PipelineBuilds;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ScriptBuild), "script")]
public abstract record PipelineBuild(Id<PipelineBuild> Id, string Name, Id<Pipeline> PipelineId) : IHasId<Id<PipelineBuild>>;

public record ScriptBuild(
    Id<PipelineBuild> Id,
    string Name,
    Id<Pipeline> PipelineId,
    string Script) : PipelineBuild(Id, Name, PipelineId);
