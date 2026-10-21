using System.Text.Json.Serialization;

namespace Olve.Pipelines.Shared.Persistence;

[JsonSerializable(typeof(PipelineVersionIndexSnapshot))]
[JsonSerializable(typeof(PipelineStateSnapshot))]
[JsonSerializable(typeof(RunIndexSnapshot))]
[JsonSerializable(typeof(RunDocument))]
internal partial class RunPersistenceJsonContext : JsonSerializerContext;
