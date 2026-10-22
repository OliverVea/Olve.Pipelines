using System.Net.Http.Headers;
using Olve.Pipelines.Cli.Diagnostics;

namespace Olve.Pipelines.Cli.Api;

/// <summary>
/// Keeps the config-stored bearer fresh for commands that outlive it (<c>pl wait</c>, <c>pl watch</c>,
/// <c>step logs -f</c>): refreshes before a request when the token is about to expire.
/// </summary>
public sealed class RefreshingAuthHandler(CliConfig config, IConsoleLog log, HttpMessageHandler? inner = null)
    : DelegatingHandler(inner ?? new HttpClientHandler())
{
    private readonly SemaphoreSlim _refresh = new(1, 1);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await _refresh.WaitAsync(ct);
        try
        {
            await TokenRefresh.RefreshIfExpiringAsync(config, log, ct);
        }
        finally
        {
            _refresh.Release();
        }

        if (config.Auth?.AccessToken is { Length: > 0 } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, ct);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _refresh.Dispose();
        base.Dispose(disposing);
    }
}
