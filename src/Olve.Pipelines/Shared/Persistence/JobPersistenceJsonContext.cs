using System.Text.Json.Serialization;

namespace Olve.Pipelines.Shared.Persistence;

[JsonSerializable(typeof(JobSnapshot))]
internal partial class JobPersistenceJsonContext : JsonSerializerContext;
