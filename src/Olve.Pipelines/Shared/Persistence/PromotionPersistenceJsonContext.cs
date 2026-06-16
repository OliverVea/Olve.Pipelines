using System.Text.Json.Serialization;

namespace Olve.Pipelines.Shared.Persistence;

[JsonSerializable(typeof(PromotionSnapshot))]
internal partial class PromotionPersistenceJsonContext : JsonSerializerContext;
