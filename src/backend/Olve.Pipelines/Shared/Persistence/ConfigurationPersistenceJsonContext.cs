using System.Text.Json.Serialization;

namespace Olve.Pipelines.Shared.Persistence;

[JsonSerializable(typeof(ConfigurationSnapshot))]
internal partial class ConfigurationPersistenceJsonContext : JsonSerializerContext;
