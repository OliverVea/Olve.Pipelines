using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Olve.Pipelines.Cli.Api.Contracts;

namespace Olve.Pipelines.Cli.Api;

/// <summary>
/// AOT-safe OIDC helpers for the auth-code + PKCE login flow: discovery, the token exchange, and
/// refresh. All JSON goes through <see cref="CliJsonContext"/> (no reflection); token requests are
/// <c>application/x-www-form-urlencoded</c>. Public-client only — no client secret is ever sent.
/// </summary>
public static class OidcClient
{
    /// <summary>Fetches <c>{authority}/.well-known/openid-configuration</c> and validates the endpoints we use.</summary>
    public static async Task<Result<OidcDiscovery>> DiscoverAsync(HttpClient http, string authority, CancellationToken ct)
    {
        var url = authority.TrimEnd('/') + "/.well-known/openid-configuration";
        try
        {
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return new ResultProblem("OIDC discovery failed: {0} {1} ({2}).", (int)response.StatusCode, response.ReasonPhrase ?? "", url);

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var disco = await JsonSerializer.DeserializeAsync(stream, CliJsonContext.Default.OidcDiscovery, ct);
            if (disco?.AuthorizationEndpoint is not { Length: > 0 } || disco.TokenEndpoint is not { Length: > 0 })
                return new ResultProblem("OIDC discovery document at {0} is missing authorization/token endpoints.", url);

            return Result.Success(disco);
        }
        catch (HttpRequestException ex)
        {
            return new ResultProblem("OIDC discovery request to {0} failed: {1}", url, ex.Message);
        }
        catch (JsonException ex)
        {
            return new ResultProblem("OIDC discovery document at {0} is not valid JSON: {1}", url, ex.Message);
        }
    }

    public static Task<Result<TokenResponse>> ExchangeCodeAsync(
        HttpClient http, string tokenEndpoint, string clientId, string code, string redirectUri, string codeVerifier, CancellationToken ct) =>
        PostTokenAsync(http, tokenEndpoint, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = codeVerifier,
        }, ct);

    public static Task<Result<TokenResponse>> RefreshAsync(
        HttpClient http, string tokenEndpoint, string clientId, string refreshToken, CancellationToken ct) =>
        PostTokenAsync(http, tokenEndpoint, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
        }, ct);

    private static async Task<Result<TokenResponse>> PostTokenAsync(
        HttpClient http, string tokenEndpoint, Dictionary<string, string> form, CancellationToken ct)
    {
        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var response = await http.PostAsync(tokenEndpoint, content, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            TokenResponse? token = null;
            try { token = JsonSerializer.Deserialize(body, CliJsonContext.Default.TokenResponse); }
            catch (JsonException) { /* fall through to error handling below */ }

            if (!response.IsSuccessStatusCode || token?.Error is { Length: > 0 })
            {
                var detail = token?.ErrorDescription ?? token?.Error ?? Snippet(body);
                return new ResultProblem("Token request failed ({0}): {1}", (int)response.StatusCode, detail);
            }

            if (token?.AccessToken is not { Length: > 0 })
                return new ResultProblem("Token response from {0} had no access_token.", tokenEndpoint);

            return Result.Success(token);
        }
        catch (HttpRequestException ex)
        {
            return new ResultProblem("Token request to {0} failed: {1}", tokenEndpoint, ex.Message);
        }
    }

    /// <summary>A high-entropy PKCE code verifier (43 base64url chars from 32 random bytes).</summary>
    public static string CreateCodeVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>The S256 code challenge for a verifier: base64url(SHA256(verifier)).</summary>
    public static string CodeChallenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Snippet(string body)
    {
        var trimmed = body.Trim();
        return trimmed.Length <= 300 ? trimmed : trimmed[..300] + "…";
    }
}
