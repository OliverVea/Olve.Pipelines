using System.Text.Json.Serialization;

namespace Olve.Pipelines.Pipelines.Persistence;

[JsonSerializable(typeof(PipelinePersistedData[]))]
internal partial class PipelinePersistenceJsonContext : JsonSerializerContext;
