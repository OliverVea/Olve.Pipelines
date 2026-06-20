using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Api.Contracts;
using Olve.Pipelines.Cli.Diagnostics;

namespace Olve.Pipelines.Cli.Commands;

/// <summary>
/// <c>pl login</c> — browser-based OIDC login (authorization code + PKCE) against the server's
/// public frontend Authentik client. Discovers authority + client_id from
/// <c>/api/config/frontend</c>, runs the redirect on a loopback <see cref="HttpListener"/>, and
/// persists the resulting token set to <c>~/.pl</c>. No client secret — the binary stays public.
/// </summary>
public sealed class LoginCommand : ICliCommand
{
    private const string Scope = "openid profile email offline_access";

    public string Noun => "login";
    public string Verb => "";
    public string HelpLine => "Log in via the browser (OIDC auth-code + PKCE) and cache the token";
    public string? HelpDetail =>
        """
        pl login   Log in via the browser and cache the token in ~/.pl

        Discovers the OIDC client from the target server's /api/config/frontend, opens your
        browser to authenticate, and stores the resulting access/refresh tokens (0600).

        Use --api-url (or PIPELINES_API_URL) to log in to a specific environment, e.g.
          pl --api-url https://pipelines-beta.ovea.pro login
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        // 1. Discover which public OIDC client this server expects (authority + client_id).
        if ((await ctx.Api.GetFrontendConfigRaw(ct)).TryPickProblems(out var cfgProblems, out var cfgJson))
            return cfgProblems;

        FrontendOidcConfig? frontend;
        try { frontend = JsonSerializer.Deserialize(cfgJson, CliJsonContext.Default.FrontendOidcConfig); }
        catch (JsonException ex) { return new ResultProblem("Could not parse /api/config/frontend: {0}", ex.Message); }

        if (frontend?.OidcAuthority is not { Length: > 0 } authority || frontend.OidcClientId is not { Length: > 0 } clientId)
            return new ResultProblem(
                "This server exposes no frontend OIDC config (Auth:FrontendAuthority/Audience unset) — cannot log in. Use --token instead.");

        using var http = new HttpClient();

        // 2. OIDC discovery → authorization + token endpoints.
        if ((await OidcClient.DiscoverAsync(http, authority, ct)).TryPickProblems(out var discoProblems, out var disco))
            return discoProblems;

        // 3. PKCE pair + anti-forgery state.
        var verifier = OidcClient.CreateCodeVerifier();
        var challenge = OidcClient.CodeChallenge(verifier);
        var state = OidcClient.Base64Url(RandomNumberGenerator.GetBytes(16));

        // 4. Loopback redirect listener on a random free port.
        var port = GetFreePort();
        var redirectUri = $"http://127.0.0.1:{port}/callback";
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try { listener.Start(); }
        catch (HttpListenerException ex) { return new ResultProblem("Could not start loopback listener for {0}: {1}", redirectUri, ex.Message); }

        // 5. Open the browser (and always print the URL as an SSH/headless fallback).
        var authorizeUrl = BuildAuthorizeUrl(disco.AuthorizationEndpoint!, clientId, redirectUri, challenge, state);
        Console.Error.WriteLine("Opening your browser to log in. If it doesn't open, visit:");
        Console.Error.WriteLine();
        Console.Error.WriteLine($"  {authorizeUrl}");
        Console.Error.WriteLine();
        TryOpenBrowser(authorizeUrl, ctx.Log);
        Console.Error.WriteLine("Waiting for the authentication redirect…");

        // 6. Capture ?code&state on the loopback.
        if ((await WaitForCodeAsync(listener, state, ct)).TryPickProblems(out var codeProblems, out var code))
            return codeProblems;

        // 7. Exchange the code (+ verifier) for tokens.
        if ((await OidcClient.ExchangeCodeAsync(http, disco.TokenEndpoint!, clientId, code, redirectUri, verifier, ct))
            .TryPickProblems(out var tokenProblems, out var token))
            return tokenProblems;

        // 8. Persist token set + the api-url it belongs to (tokens are environment-scoped).
        var apiUrl = ApiClientFactory.ResolveApiUrl(cli, ctx.Config);
        ctx.Config.ApiUrl = apiUrl;
        ctx.Config.Auth = new AuthConfig
        {
            AccessToken = token.AccessToken,
            RefreshToken = token.RefreshToken,
            TokenEndpoint = disco.TokenEndpoint,
            ClientId = clientId,
            ExpiresAt = token.ExpiresIn is { } seconds ? DateTimeOffset.UtcNow.AddSeconds(seconds) : null,
        };
        if (CliConfigStore.Save(ctx.Config).TryPickProblems(out var saveProblems))
            return saveProblems;

        var result = new LoginResult
        {
            ApiUrl = apiUrl,
            Authority = authority,
            ExpiresAt = ctx.Config.Auth.ExpiresAt,
            HasRefreshToken = token.RefreshToken is { Length: > 0 },
        };
        ctx.Output.Emit(result, CliJsonContext.Default.LoginResult, () =>
        {
            ctx.Output.Line($"Logged in to {apiUrl}.");
            ctx.Output.Line($"Token saved to {CliConfigStore.ResolvePath()}.");
            if (result.ExpiresAt is { } expiresAt)
                ctx.Output.Line($"Access token expires {expiresAt:u}.");
            if (!result.HasRefreshToken)
                ctx.Output.Line("Warning: no refresh token issued (offline_access not granted) — re-run `pl login` when it expires.");
        });
        return Result.Success();
    }

    private static string BuildAuthorizeUrl(string authorizationEndpoint, string clientId, string redirectUri, string challenge, string state)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = Scope,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        var qs = string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return $"{authorizationEndpoint}?{qs}";
    }

    private static async Task<Result<string>> WaitForCodeAsync(HttpListener listener, string expectedState, CancellationToken ct)
    {
        // Stopping the listener on cancellation unblocks GetContextAsync (which takes no token).
        using var registration = ct.Register(() => { try { listener.Stop(); } catch (ObjectDisposedException) { } });

        while (true)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return ct.IsCancellationRequested
                    ? new ResultProblem("Login cancelled before the browser redirect arrived.")
                    : new ResultProblem("Loopback listener stopped before the redirect arrived: {0}", ex.Message);
            }

            var request = context.Request;
            if (request.Url?.AbsolutePath != "/callback")
            {
                Respond(context, HttpStatusCode.NotFound, "Not found.");
                continue;
            }

            var query = ParseQuery(request.Url.Query);

            if (query.TryGetValue("error", out var error))
            {
                var description = query.GetValueOrDefault("error_description", error);
                Respond(context, HttpStatusCode.OK, "Login failed. You can close this tab and return to the terminal.");
                return new ResultProblem("Authorization failed: {0} — {1}", error, description);
            }

            if (!query.TryGetValue("state", out var returnedState) || returnedState != expectedState)
            {
                Respond(context, HttpStatusCode.BadRequest, "State mismatch. You can close this tab.");
                return new ResultProblem("OAuth state mismatch — aborting (possible CSRF or a stale redirect).");
            }

            if (!query.TryGetValue("code", out var code) || code.Length == 0)
            {
                Respond(context, HttpStatusCode.BadRequest, "Missing authorization code. You can close this tab.");
                return new ResultProblem("Authorization redirect carried no code.");
            }

            Respond(context, HttpStatusCode.OK, "Login successful. You can close this tab and return to the terminal.");
            return Result.Success(code);
        }
    }

    private static void Respond(HttpListenerContext context, HttpStatusCode status, string message)
    {
        try
        {
            context.Response.StatusCode = (int)status;
            context.Response.ContentType = "text/html; charset=utf-8";
            var html = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>pl login</title></head>" +
                       $"<body style=\"font-family:system-ui;max-width:32rem;margin:4rem auto;text-align:center\">" +
                       $"<h2>{WebUtility.HtmlEncode(message)}</h2></body></html>";
            var bytes = Encoding.UTF8.GetBytes(html);
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes);
            context.Response.OutputStream.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException)
        {
            // The browser may have already disconnected — the code is what matters, not this page.
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0)
                result[Uri.UnescapeDataString(pair)] = "";
            else
                result[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return result;
    }

    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    private static void TryOpenBrowser(string url, IConsoleLog log)
    {
        try
        {
            if (OperatingSystem.IsLinux())
                Process.Start("xdg-open", url);
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", url);
            else if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            log.Log($"could not auto-open browser: {ex.Message}");
        }
    }
}
