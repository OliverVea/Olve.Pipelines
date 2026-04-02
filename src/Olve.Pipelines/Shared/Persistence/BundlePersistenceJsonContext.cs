using System.Text.Json.Serialization;
using Olve.Pipelines.Building;
using Olve.Pipelines.Pipelines;

namespace Olve.Pipelines.Shared.Persistence;

public record ArtifactBundlePersistedData(Id<ArtifactBundle> Id, Id<Pipeline> PipelineId, DateTimeOffset CreatedAt);

[JsonSerializable(typeof(ArtifactBundlePersistedData))]
internal partial class BundlePersistenceJsonContext : JsonSerializerContext;
