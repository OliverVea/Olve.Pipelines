using System.Text.Json;
using System.Text.Json.Serialization;

namespace Olve.Pipelines.Pipelines.Polling;

[JsonSerializable(typeof(JsonElement))]
internal partial class PollJsonContext : JsonSerializerContext;
