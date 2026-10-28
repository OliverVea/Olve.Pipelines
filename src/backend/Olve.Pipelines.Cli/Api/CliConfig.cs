using System.Text.Json.Serialization;

namespace Olve.Pipelines.Cli.Api;

/// <summary>
/// One profile of the CLI config (<c>~/.pl</c>): the API base URL and cached OIDC tokens. Written by
/// <c>pl login</c>, read by <see cref="ApiClientFactory"/>. Mutable POCO for JSON round-trip.
/// </summary>
public sealed class CliConfig
{
    /// <summary>The profile this config was loaded from (not stored inside it).</summary>
    [JsonIgnore]
    public string Profile { get; set; } = CliConfigStore.DefaultProfile;

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

/// <summary>The <c>~/.pl</c> file: named profiles. <see cref="ApiUrl"/>/<see cref="Auth"/> are the pre-profile shape, read as <c>default</c>.</summary>
public sealed class CliConfigFile
{
    public Dictionary<string, CliConfig>? Profiles { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiUrl { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AuthConfig? Auth { get; set; }
}
