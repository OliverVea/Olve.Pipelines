namespace Olve.Pipelines.Cli.Api;

/// <summary>
/// Persisted CLI config (<c>~/.pl</c>): the API base URL and cached OIDC tokens. Written by
/// <c>pl login</c>, read by <see cref="ApiClientFactory"/>. Mutable POCO for JSON round-trip.
/// </summary>
public sealed class CliConfig
{
    public string? ApiUrl { get; set; }
    public AuthConfig? Auth { get; set; }
}

/// <summary>Cached OIDC token set + the endpoint/client needed to refresh it.</summary>
public sealed class AuthConfig
{
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public string? TokenEndpoint { get; set; }
    public string? ClientId { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}
