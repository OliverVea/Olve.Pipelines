using System.Text.Json.Serialization;

namespace Olve.Pipelines.Cli.Api.Contracts;

/// <summary>
/// The <c>/api/config/frontend</c> payload — which public OIDC client the CLI should use to log
/// in. The source of truth for authority + client_id (so beta/prod differences are automatic).
/// </summary>
public sealed class FrontendOidcConfig
{
    [JsonPropertyName("oidcAuthority")] public string? OidcAuthority { get; set; }
    [JsonPropertyName("oidcClientId")] public string? OidcClientId { get; set; }
}

/// <summary>The subset of the OIDC discovery document the login flow needs. Snake-case wire names.</summary>
public sealed class OidcDiscovery
{
    [JsonPropertyName("authorization_endpoint")] public string? AuthorizationEndpoint { get; set; }
    [JsonPropertyName("token_endpoint")] public string? TokenEndpoint { get; set; }
    [JsonPropertyName("device_authorization_endpoint")] public string? DeviceAuthorizationEndpoint { get; set; }
}

/// <summary>
/// The device-authorization response (RFC 8628) — returned by the device endpoint to start the
/// headless/SSH login flow. The user opens <see cref="VerificationUriComplete"/> (or
/// <see cref="VerificationUri"/> and enters <see cref="UserCode"/>) while the CLI polls the token
/// endpoint with <see cref="DeviceCode"/>. Snake-case wire names.
/// </summary>
public sealed class DeviceAuthResponse
{
    [JsonPropertyName("device_code")] public string? DeviceCode { get; set; }
    [JsonPropertyName("user_code")] public string? UserCode { get; set; }
    [JsonPropertyName("verification_uri")] public string? VerificationUri { get; set; }
    [JsonPropertyName("verification_uri_complete")] public string? VerificationUriComplete { get; set; }
    [JsonPropertyName("expires_in")] public int? ExpiresIn { get; set; }
    [JsonPropertyName("interval")] public int? Interval { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
}

/// <summary>The OAuth2 token endpoint response (auth-code exchange and refresh). Snake-case wire names.</summary>
public sealed class TokenResponse
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    [JsonPropertyName("token_type")] public string? TokenType { get; set; }
    [JsonPropertyName("expires_in")] public int? ExpiresIn { get; set; }
    [JsonPropertyName("scope")] public string? Scope { get; set; }
    [JsonPropertyName("id_token")] public string? IdToken { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
}

/// <summary>Machine-readable summary emitted by <c>pl login --json</c>.</summary>
public sealed class LoginResult
{
    public string? ApiUrl { get; set; }
    public string? Authority { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public bool HasRefreshToken { get; set; }
}
