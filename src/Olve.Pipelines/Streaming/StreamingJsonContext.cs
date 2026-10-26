using System.Text.Json;
using System.Text.Json.Serialization;

namespace Olve.Pipelines.Streaming;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(RunChanged))]
[JsonSerializable(typeof(AttemptChanged))]
[JsonSerializable(typeof(VersionChanged))]
[JsonSerializable(typeof(BlockedStepsChanged))]
[JsonSerializable(typeof(BindingChanged))]
[JsonSerializable(typeof(PipelineChanged))]
[JsonSerializable(typeof(ConfigChanged))]
[JsonSerializable(typeof(PendingChanged))]
[JsonSerializable(typeof(QueuedChanged))]
[JsonSerializable(typeof(SecretsChanged))]
[JsonSerializable(typeof(PollChanged))]
[JsonSerializable(typeof(LogChunk))]
[JsonSerializable(typeof(LogStreamEnd))]
[JsonSerializable(typeof(StreamHello))]
[JsonSerializable(typeof(StreamResync))]
internal partial class StreamingJsonContext : JsonSerializerContext;
