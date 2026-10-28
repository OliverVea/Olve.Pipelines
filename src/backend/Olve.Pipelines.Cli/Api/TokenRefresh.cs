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

    // After a refused refresh, don't ask again on every request of a long-running command.
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(1);
    private static DateTimeOffset _lastFailure = DateTimeOffset.MinValue;

    public static async Task EnsureFreshAsync(CliArgs cli, CliConfig config, IConsoleLog log, CancellationToken ct)
    {
        if (OwnsToken(cli))
            await RefreshIfExpiringAsync(config, log, ct);
    }

    /// <summary>Only the config-stored token is ours to refresh; an explicit --token/env token is the user's.</summary>
    public static bool OwnsToken(CliArgs cli) =>
        cli.Option("token") is not { Length: > 0 } && Environment.GetEnvironmentVariable(ApiClientFactory.TokenEnvVar) is not { Length: > 0 };

    public static async Task RefreshIfExpiringAsync(CliConfig config, IConsoleLog log, CancellationToken ct)
    {
        if (!Refreshable(config.Auth) || !Expiring(config.Auth!) || DateTimeOffset.UtcNow - _lastFailure < RetryAfterFailure)
            return;

        IDisposable fileLock;
        try
        {
            fileLock = CliConfigStore.Lock();
        }
        catch (IOException ex)
        {
            log.Log($"token refresh skipped: {ex.Message}");
            return;
        }

        using (fileLock)
        {
            // Another pl process may have refreshed meanwhile: its refresh token replaced ours (rotation),
            // and reusing the old one would fail, or revoke the session.
            if (!CliConfigStore.Load(config.Profile).TryPickProblems(out _, out var onDisk) && onDisk.Auth is { } current)
            {
                config.Auth = current;
                if (!Expiring(current))
                {
                    log.Log("access token already refreshed by another pl process");
                    return;
                }
            }

            var auth = config.Auth!;
            if (!Refreshable(auth))
                return;

            log.Log("access token expired or expiring — refreshing via refresh_token");
            using var http = new HttpClient();
            var result = await OidcClient.RefreshAsync(http, auth.TokenEndpoint!, auth.ClientId!, auth.RefreshToken!, ct);
            if (result.TryPickProblems(out var problems, out var token))
            {
                _lastFailure = DateTimeOffset.UtcNow;
                log.Log($"token refresh failed (continuing with stale token): {string.Join("; ", problems.Select(p => p.ToString()))}");
                return;
            }

            auth.AccessToken = token.AccessToken;
            if (token.RefreshToken is { Length: > 0 } rotated)
                auth.RefreshToken = rotated;
            auth.ExpiresAt = token.ExpiresIn is { } seconds ? DateTimeOffset.UtcNow.AddSeconds(seconds) : null;

            if (CliConfigStore.SaveLocked(config).TryPickProblems(out var saveProblems))
                log.Log($"refreshed token but could not persist it: {string.Join("; ", saveProblems.Select(p => p.ToString()))}");
            else
                log.Log("access token refreshed");
        }
    }

    private static bool Refreshable(AuthConfig? auth) =>
        auth is { RefreshToken.Length: > 0, TokenEndpoint.Length: > 0, ClientId.Length: > 0 };

    private static bool Expiring(AuthConfig auth) =>
        auth.ExpiresAt is not { } expiresAt || expiresAt <= DateTimeOffset.UtcNow.AddSeconds(SkewSeconds);
}
