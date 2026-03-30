using System.Text.Json.Serialization;

namespace Olve.Pipelines.Kubernetes;

public record OpenBaoLoginRequest(
    [property: JsonPropertyName("jwt")] string Jwt,
    [property: JsonPropertyName("role")] string Role);

public record OpenBaoLoginResponse(
    [property: JsonPropertyName("auth")] OpenBaoAuth? Auth);

public record OpenBaoAuth(
    [property: JsonPropertyName("client_token")] string? ClientToken,
    [property: JsonPropertyName("lease_duration")] int LeaseDuration);

public record OpenBaoSecretResponse(
    [property: JsonPropertyName("data")] OpenBaoSecretData? Data);

public record OpenBaoSecretData(
    [property: JsonPropertyName("data")] Dictionary<string, string>? Data);

[JsonSerializable(typeof(OpenBaoLoginRequest))]
[JsonSerializable(typeof(OpenBaoLoginResponse))]
[JsonSerializable(typeof(OpenBaoSecretResponse))]
internal partial class OpenBaoJsonContext : JsonSerializerContext;
