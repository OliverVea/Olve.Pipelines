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

    /// <summary>Starts a device-authorization grant (RFC 8628): returns the user code + verification URI.</summary>
    public static async Task<Result<DeviceAuthResponse>> StartDeviceAuthAsync(
        HttpClient http, string deviceEndpoint, string clientId, string scope, CancellationToken ct)
    {
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scope"] = scope,
            });
            using var response = await http.PostAsync(deviceEndpoint, content, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            DeviceAuthResponse? device = null;
            try { device = JsonSerializer.Deserialize(body, CliJsonContext.Default.DeviceAuthResponse); }
            catch (JsonException) { /* fall through */ }

            if (!response.IsSuccessStatusCode || device?.Error is { Length: > 0 })
            {
                var detail = device?.ErrorDescription ?? device?.Error ?? Snippet(body);
                return new ResultProblem("Device authorization request failed ({0}): {1}", (int)response.StatusCode, detail);
            }

            if (device?.DeviceCode is not { Length: > 0 } || device.UserCode is not { Length: > 0 })
                return new ResultProblem("Device authorization response from {0} was missing device_code/user_code.", deviceEndpoint);

            return Result.Success(device);
        }
        catch (HttpRequestException ex)
        {
            return new ResultProblem("Device authorization request to {0} failed: {1}", deviceEndpoint, ex.Message);
        }
    }

    /// <summary>
    /// Polls the token endpoint for a device grant until the user approves, the code expires, or it's
    /// denied. Honours <c>authorization_pending</c> (keep waiting) and <c>slow_down</c> (back off).
    /// </summary>
    public static async Task<Result<TokenResponse>> PollDeviceTokenAsync(
        HttpClient http, string tokenEndpoint, string clientId, string deviceCode, int intervalSeconds, CancellationToken ct)
    {
        var interval = Math.Max(1, intervalSeconds);
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);

            var (status, token, body) = await PostFormAsync(http, tokenEndpoint, new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = deviceCode,
                ["client_id"] = clientId,
            }, ct);

            if (token?.AccessToken is { Length: > 0 })
                return Result.Success(token);

            switch (token?.Error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += 5;
                    continue;
                case "expired_token":
                    return new ResultProblem("The device code expired before you approved the login. Run `pl login --device` again.");
                case "access_denied":
                    return new ResultProblem("Login was denied in the browser.");
                default:
                    var detail = token?.ErrorDescription ?? token?.Error ?? Snippet(body);
                    return new ResultProblem("Device token poll failed ({0}): {1}", (int)status, detail);
            }
        }
    }

    private static async Task<Result<TokenResponse>> PostTokenAsync(
        HttpClient http, string tokenEndpoint, Dictionary<string, string> form, CancellationToken ct)
    {
        try
        {
            var (status, token, body) = await PostFormAsync(http, tokenEndpoint, form, ct);

            if (!IsSuccess(status) || token?.Error is { Length: > 0 })
            {
                var detail = token?.ErrorDescription ?? token?.Error ?? Snippet(body);
                return new ResultProblem("Token request failed ({0}): {1}", (int)status, detail);
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

    /// <summary>Low-level token-endpoint POST. Returns the status + parsed body without judging success,
    /// so callers (e.g. the device poll) can inspect <c>authorization_pending</c>/<c>slow_down</c>.</summary>
    private static async Task<(System.Net.HttpStatusCode Status, TokenResponse? Token, string Body)> PostFormAsync(
        HttpClient http, string tokenEndpoint, Dictionary<string, string> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await http.PostAsync(tokenEndpoint, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        TokenResponse? token = null;
        try { token = JsonSerializer.Deserialize(body, CliJsonContext.Default.TokenResponse); }
        catch (JsonException) { /* caller handles the missing/garbled body */ }

        return (response.StatusCode, token, body);
    }

    private static bool IsSuccess(System.Net.HttpStatusCode status) => (int)status is >= 200 and < 300;

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
