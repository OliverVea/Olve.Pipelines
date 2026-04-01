using System.Text.Json.Serialization;
using Olve.Pipelines.Building;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Sourcing;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.Shared.Persistence;

public record SourceBundlePersistedData(Id<SourceBundle> Id, Id<Pipeline> PipelineId, DateTimeOffset CreatedAt);

public record ArtifactBundlePersistedData(Id<ArtifactBundle> Id, Id<Pipeline> PipelineId, Id<SourceBundle> SourceBundleId, DateTimeOffset CreatedAt);

[JsonSerializable(typeof(SourceBundlePersistedData))]
[JsonSerializable(typeof(ArtifactBundlePersistedData))]
internal partial class BundlePersistenceJsonContext : JsonSerializerContext;
