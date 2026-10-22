using Amazon.S3;
using Olve.Pipelines.Pipelines.Runs;

// `Results` alone resolves to the Olve.Results namespace (global using), so alias the ASP.NET one.
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace Olve.Pipelines.Distribution;

/// <summary>
/// Anonymous downloads of the <c>pl</c> operator CLI, so an operator can bootstrap the CLI with a single
/// <c>curl</c> — before they have a token (hence anonymous).
/// <list type="bullet">
/// <item><c>GET|HEAD /download/{asset}</c>: an output file of the latest successful <c>publish-cli</c> attempt
/// of the configured pipeline (<see cref="CliReleaseQuery"/>), streamed from this instance's Garage (run file
/// storage, <c>runs/&lt;p&gt;/&lt;run&gt;/publish-cli/&lt;attempt&gt;/files/&lt;asset&gt;</c>). Anything else under
/// <c>/download/</c> is a 404, never the SPA.</item>
/// <item><c>GET /install.sh</c>, <c>GET /install.ps1</c>: the repo's install scripts with their default
/// instance replaced by this one (<c>Links:BaseUrl</c>, else the request's origin), so
/// <c>curl -fsSL &lt;instance&gt;/install.sh | sh</c> installs this instance's <c>pl</c>.</item>
/// </list>
/// None of these are API operations, so they are kept out of the OpenAPI document.
/// </summary>
public static class CliDownloadEndpoints
{
    /// <summary>
    /// The allow-list is the security boundary: only these names resolve, so the route can never read other
    /// files of the attempt, let alone arbitrary keys of the bucket.
    /// </summary>
    internal static readonly IReadOnlySet<string> Assets = new HashSet<string>(StringComparer.Ordinal)
    {
        "pl-linux-x64",
        "pl-win-x64.exe",
    };

    /// <summary>The default instance written in <c>install.sh</c> / <c>install.ps1</c> (v1 prod over Tailscale).</summary>
    internal const string ScriptDefaultBaseUrl = "https://pipelines-private.ovea.pro";

    public static void MapCliDownloadEndpoints(this WebApplication app)
    {
        app.MapMethods("/download/{**asset}", [HttpMethods.Get, HttpMethods.Head], async Task<IResult> (
            string? asset,
            HttpContext http,
            IServiceProvider services,
            CancellationToken ct) =>
        {
            if (asset is null || !Assets.Contains(asset))
                return HttpResults.NotFound();

            // IAmazonS3 is only registered when storage is configured (not in the ephemeral doc-generation /
            // no-storage profiles), so resolve optionally and degrade to 503 instead of a DI failure.
            if (services.GetService<IAmazonS3>() is null)
                return HttpResults.Problem("Storage not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);

            // Not published yet (or Distribution:CliPipeline unset).
            if (!services.GetRequiredService<CliReleaseQuery>().TryGetLatest(out var attempt))
                return HttpResults.NotFound();

            var files = services.GetRequiredService<AttemptFileService>();
            http.Response.Headers.ContentDisposition = $"attachment; filename=\"{asset}\"";
            http.Response.Headers["X-Olve-Pl-Run"] = attempt.RunNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (HttpMethods.IsHead(http.Request.Method))
            {
                if (await files.GetSizeAsync(attempt, asset, ct) is not { } size)
                    return HttpResults.NotFound();
                http.Response.ContentType = "application/octet-stream";
                http.Response.ContentLength = size;
                return HttpResults.Empty;
            }

            using var download = await files.OpenAsync(attempt, asset, ct);
            if (download is null)
                return HttpResults.NotFound();

            http.Response.ContentType = "application/octet-stream";
            http.Response.ContentLength = download.Length;
            // Stream straight from Garage to the client — never buffer the (~70 MB) binary in the pod.
            await download.Content.CopyToAsync(http.Response.Body, ct);
            return HttpResults.Empty;
        })
        .AllowAnonymous()
        .ExcludeFromDescription()
        .WithName("DownloadCli");

        app.MapGet("/install.sh", (HttpContext http, IConfiguration config) => InstallScript(http, config, "install.sh", "text/x-shellscript; charset=utf-8"))
            .AllowAnonymous()
            .ExcludeFromDescription()
            .WithName("CliInstallScript");

        app.MapGet("/install.ps1", (HttpContext http, IConfiguration config) => InstallScript(http, config, "install.ps1", "text/plain; charset=utf-8"))
            .AllowAnonymous()
            .ExcludeFromDescription()
            .WithName("CliInstallScriptWindows");
    }

    private static IResult InstallScript(HttpContext http, IConfiguration config, string name, string contentType)
    {
        var path = Path.Combine(AppContext.BaseDirectory, name);
        if (!File.Exists(path))
            return HttpResults.NotFound();

        var script = RewriteDefaultBaseUrl(File.ReadAllText(path), InstanceBaseUrl(http, config));
        return HttpResults.Text(script, contentType);
    }

    /// <summary><c>Links:BaseUrl</c> (the public URL), else the request's own origin.</summary>
    internal static string InstanceBaseUrl(HttpContext http, IConfiguration config)
    {
        var configured = config["Links:BaseUrl"];
        return string.IsNullOrWhiteSpace(configured)
            ? $"{http.Request.Scheme}://{http.Request.Host}"
            : configured.TrimEnd('/');
    }

    /// <summary>Replaces the script's default instance; an <c>PL_API_URL</c> override still wins.</summary>
    internal static string RewriteDefaultBaseUrl(string script, string baseUrl) =>
        script.Replace(ScriptDefaultBaseUrl, baseUrl, StringComparison.Ordinal);
}
