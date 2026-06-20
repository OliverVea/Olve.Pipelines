using Olve.Pipelines.Cli.Diagnostics;

namespace Olve.Pipelines.Cli.Api;

/// <summary>
/// Best-effort, pre-flight refresh of the cached <c>~/.pl</c> access token. Runs before the API
/// client is built so commands always go out with a fresh bearer. Only touches the config-stored
/// token (never a <c>--token</c>/env token, which the user owns) and never fails the command — a
/// refused refresh just leaves the stale token in place, and the API surfaces a clear 401.
/// </summary>
public static class TokenRefresh
{
    /// <summary>Seconds of remaining lifetime under which the access token is considered "expiring".</summary>
    private const int SkewSeconds = 60;

    public static async Task EnsureFreshAsync(CliArgs cli, CliConfig config, IConsoleLog log, CancellationToken ct)
    {
        // Only the config-stored token is ours to refresh; an explicit --token/env token is the user's.
        if (cli.Option("token") is { Length: > 0 })
            return;
        if (Environment.GetEnvironmentVariable(ApiClientFactory.TokenEnvVar) is { Length: > 0 })
            return;

        if (config.Auth is not { } auth)
            return;
        if (auth.RefreshToken is not { Length: > 0 } refreshToken)
            return;
        if (auth.TokenEndpoint is not { Length: > 0 } tokenEndpoint || auth.ClientId is not { Length: > 0 } clientId)
            return;
        if (auth.ExpiresAt is { } expiresAt && expiresAt > DateTimeOffset.UtcNow.AddSeconds(SkewSeconds))
            return; // still comfortably valid

        log.Log("access token expired or expiring — refreshing via refresh_token");
        using var http = new HttpClient();
        var result = await OidcClient.RefreshAsync(http, tokenEndpoint, clientId, refreshToken, ct);
        if (result.TryPickProblems(out var problems, out var token))
        {
            log.Log($"token refresh failed (continuing with stale token): {string.Join("; ", problems.Select(p => p.ToString()))}");
            return;
        }

        auth.AccessToken = token.AccessToken;
        if (token.RefreshToken is { Length: > 0 } rotated)
            auth.RefreshToken = rotated; // refresh-token rotation: keep the newest
        auth.ExpiresAt = token.ExpiresIn is { } seconds ? DateTimeOffset.UtcNow.AddSeconds(seconds) : null;

        if (CliConfigStore.Save(config).TryPickProblems(out var saveProblems))
            log.Log($"refreshed token but could not persist it: {string.Join("; ", saveProblems.Select(p => p.ToString()))}");
        else
            log.Log("access token refreshed");
    }
}
