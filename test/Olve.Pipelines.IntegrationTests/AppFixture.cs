using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Olve.Pipelines.Client;
using Refit;
using TUnit.Core.Interfaces;

namespace Olve.Pipelines.IntegrationTests;

/// <summary>
/// Targets the live <b>beta</b> instance over authenticated HTTP — no Testcontainers, no Docker.
/// The suite runs as a processing step after <c>deploy-beta</c> in <c>.pipelines/config.yaml</c>
/// (gating prod), and can be run locally against beta with the same env vars.
///
/// Auth: beta validates Authentik tokens but every endpoint is currently <c>.AllowAnonymous()</c>,
/// so a token is forward-compat, not required. The fixture mints a client-credentials token when
/// <c>BETA_OIDC_CLIENT_SECRET</c> is present and otherwise runs unauthenticated — a missing secret
/// never hard-fails the suite (which would false-block prod for something beta does not enforce).
///
/// Beta availability is opt-in via <c>BETA_BASE_URL</c> (default <c>https://pipelines-beta.ovea.pro</c>).
/// Set <c>BETA_DISABLED=1</c> to force <see cref="BetaAvailable"/> off so a plain local
/// <c>dotnet test</c> skips the beta-dependent tests instead of failing.
/// </summary>
public class AppFixture : IAsyncInitializer, IAsyncDisposable
{
    private const string DefaultBaseUrl = "https://pipelines-beta.ovea.pro";
    private const string DefaultTokenUrl = "https://auth-beta.ovea.pro/application/o/token/";
    private const string DefaultClientId = "olve-pipelines-beta";

    private static readonly HttpClient TokenHttp = new();

    private string _baseUrl = null!;
    private string? _token;

    /// <summary>
    /// True when the suite has a beta instance to talk to. Beta-dependent tests guard on this and
    /// skip (not fail) when it is false, so the suite is runnable without a beta environment.
    /// </summary>
    public static bool BetaAvailable =>
        Environment.GetEnvironmentVariable("BETA_DISABLED") is not "1" and not "true";

    public async Task InitializeAsync()
    {
        _baseUrl = (Environment.GetEnvironmentVariable("BETA_BASE_URL") ?? DefaultBaseUrl).TrimEnd('/');
        _token = await TryMintTokenAsync();
    }

    /// <summary>Refit client over the generated API surface, with a bearer token when one was minted.</summary>
    public IOlvePipelinesv1 CreateApiClient() =>
        RestService.For<IOlvePipelinesv1>(CreateAuthenticatedHttpClient());

    /// <summary>HTTP client with the minted bearer token attached (no token when none was available).</summary>
    public HttpClient CreateAuthenticatedHttpClient()
    {
        var client = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        if (_token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return client;
    }

    public HttpClient CreateUnauthenticatedHttpClient() =>
        new() { BaseAddress = new Uri(_baseUrl) };

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Mints an Authentik client-credentials access token for the beta API audience, or returns null
    /// if no client secret is configured (or the mint fails) — callers then run unauthenticated.
    /// </summary>
    private static async Task<string?> TryMintTokenAsync()
    {
        var clientSecret = Environment.GetEnvironmentVariable("BETA_OIDC_CLIENT_SECRET");
        if (string.IsNullOrEmpty(clientSecret))
            return null;

        var tokenUrl = Environment.GetEnvironmentVariable("BETA_OIDC_TOKEN_URL") ?? DefaultTokenUrl;
        var clientId = Environment.GetEnvironmentVariable("BETA_OIDC_CLIENT_ID") ?? DefaultClientId;

        try
        {
            using var response = await TokenHttp.PostAsync(tokenUrl, new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                    ["scope"] = "openid profile email",
                }));

            if (!response.IsSuccessStatusCode)
                return null;

            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            return payload.TryGetProperty("access_token", out var token) ? token.GetString() : null;
        }
        catch
        {
            // A token is optional (beta does not enforce auth); never block the suite on minting.
            return null;
        }
    }
}
