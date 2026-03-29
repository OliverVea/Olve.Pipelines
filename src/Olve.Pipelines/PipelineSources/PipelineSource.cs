using System.Text.Json.Serialization;
using Olve.Pipelines.Pipelines;
using Olve.Utilities.Ids;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.PipelineSources;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(GitHubRepositorySource), "github")]
public abstract record PipelineSource(Id<PipelineSource> Id, string Name, Id<Pipeline> PipelineId) : IHasId<Id<PipelineSource>>;

public record GitHubRepositorySource(
    Id<PipelineSource> Id,
    string Name,
    Id<Pipeline> PipelineId,
    string Owner,
    string Repository,
    string Branch) : PipelineSource(Id, Name, PipelineId);
