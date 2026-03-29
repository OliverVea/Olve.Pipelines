using System.Text.Json.Serialization;

namespace Olve.Pipelines.Shared.Persistence;

public record SourceBundlePersistedData(Guid Id, Guid PipelineId, DateTimeOffset CreatedAt);

public record ArtifactBundlePersistedData(Guid Id, Guid PipelineId, Guid SourceBundleId, DateTimeOffset CreatedAt);

[JsonSerializable(typeof(SourceBundlePersistedData))]
[JsonSerializable(typeof(ArtifactBundlePersistedData))]
internal partial class BundlePersistenceJsonContext : JsonSerializerContext;
