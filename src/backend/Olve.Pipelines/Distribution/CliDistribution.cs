using Amazon.S3;
using Olve.Pipelines.Pipelines.Runs;
using Olve.TypeSpec;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.Distribution;

/// <summary>
/// Anonymous downloads of the <c>pl</c> operator CLI, so an operator can bootstrap the CLI with a single
/// <c>curl</c> before they have a token.
/// <list type="bullet">
/// <item><c>GET|HEAD /download/{asset}</c>: an output file of the latest successful <c>publish-cli</c> attempt
/// of the configured pipeline (<see cref="CliReleaseQuery"/>), streamed from this instance's Garage. Anything
/// else under <c>/download/</c> is a 404, never the SPA.</item>
/// <item><c>GET /install.sh</c>, <c>GET /install.ps1</c>: the repo's install scripts with their default
/// instance replaced by this one (<c>Links:BaseUrl</c>, else the request's origin).</item>
/// </list>
/// </summary>
public static class CliDistribution
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

    /// <summary>The default instance written in <c>install.sh</c> / <c>install.ps1</c>.</summary>
    internal const string ScriptDefaultBaseUrl = "__OLVE_INSTANCE__";

    internal const string StillBuildingMessage = "pl is still being built on this instance; try again in a few minutes.";

    internal const int RetryAfterSeconds = 60;

    internal static string Attachment(string asset) => $"attachment; filename=\"{asset}\"";

    /// <summary>
    /// <c>Distribution:LocalDirectory</c>: binaries built next to the instance (the local compose stack builds
    /// <c>pl</c> from source) are served from there instead of a <c>publish-cli</c> run.
    /// </summary>
    internal static string? LocalAsset(IConfiguration config, string asset)
    {
        var dir = config["Distribution:LocalDirectory"];
        if (string.IsNullOrWhiteSpace(dir))
            return null;
        var path = Path.Combine(dir, asset);
        return File.Exists(path) ? path : null;
    }

    /// <summary>The local build directory is configured but does not have <paramref name="asset"/> yet.</summary>
    internal static bool LocalBuildPending(IConfiguration config, string asset)
        => !string.IsNullOrWhiteSpace(config["Distribution:LocalDirectory"]) && LocalAsset(config, asset) is null;

    /// <summary><c>Links:BaseUrl</c> (the public URL), else the request's own origin.</summary>
    internal static string InstanceBaseUrl(HttpRequest? request, IConfiguration config)
    {
        var configured = config["Links:BaseUrl"];
        return string.IsNullOrWhiteSpace(configured)
            ? $"{request?.Scheme}://{request?.Host}"
            : configured.TrimEnd('/');
    }

    /// <summary>Replaces the script's default instance; an <c>PL_API_URL</c> override still wins.</summary>
    internal static string RewriteDefaultBaseUrl(string script, string baseUrl) =>
        script.Replace(ScriptDefaultBaseUrl, baseUrl, StringComparison.Ordinal);

    /// <summary>The script, or one that says why it can't install yet: piped into a shell, an error status that <c>curl -f</c> hides would say nothing.</summary>
    internal static string? InstallScript(HttpRequest? request, IConfiguration config, string name)
    {
        var windows = name.EndsWith(".ps1", StringComparison.Ordinal);
        if (LocalBuildPending(config, windows ? "pl-win-x64.exe" : "pl-linux-x64"))
            return windows ? $"Write-Error '{StillBuildingMessage}'\nexit 1\n" : $"#!/bin/sh\necho '{StillBuildingMessage}' >&2\nexit 1\n";

        var path = Path.Combine(AppContext.BaseDirectory, name);
        if (!File.Exists(path))
            return null;

        var text = File.ReadAllText(path);
        // `curl … | sh` breaks on CRs, whatever line endings the image was built from.
        if (!windows)
            text = text.ReplaceLineEndings("\n");
        return RewriteDefaultBaseUrl(text, InstanceBaseUrl(request, config));
    }
}

// IAmazonS3 is only registered when storage is configured (not in the ephemeral doc-generation / no-storage
// profiles), so the download handlers resolve it optionally and answer 503 instead of a DI failure.
[OperationHandler]
public sealed class DownloadCliHandler(IServiceProvider services, IConfiguration config, IHttpContextAccessor http) : Contract.ICliDownloadsDownloadHandler
{
    public async Task<Contract.CliDownloadsDownloadResponse> HandleAsync(Contract.CliDownloadsDownloadRequest request, CancellationToken cancellationToken)
    {
        var asset = request.Asset;
        if (!CliDistribution.Assets.Contains(asset))
            return new Contract.CliDownloadsDownloadResponse.NotFound();

        if (CliDistribution.LocalAsset(config, asset) is { } local)
        {
            var file = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return new Contract.CliDownloadsDownloadResponse.Ok(file, CliDistribution.Attachment(asset), file.Length);
        }
        if (CliDistribution.LocalBuildPending(config, asset))
            return new Contract.CliDownloadsDownloadResponse.ServiceUnavailable(CliDistribution.StillBuildingMessage + "\n", CliDistribution.RetryAfterSeconds);
        if (services.GetService<IAmazonS3>() is null)
            return new Contract.CliDownloadsDownloadResponse.ServiceUnavailable("Storage is not configured.\n");

        // Not published yet (or Distribution:CliPipeline unset).
        if (!services.GetRequiredService<CliReleaseQuery>().TryGetLatest(out var attempt))
            return new Contract.CliDownloadsDownloadResponse.NotFound();
        var download = await services.GetRequiredService<AttemptFileService>().OpenAsync(attempt, asset, cancellationToken);
        if (download is null)
            return new Contract.CliDownloadsDownloadResponse.NotFound();

        // Streamed straight from Garage, never buffered (~70 MB); the content stream alone doesn't release the S3 response.
        http.HttpContext?.Response.RegisterForDispose(download);
        return new Contract.CliDownloadsDownloadResponse.Ok(download.Content, CliDistribution.Attachment(asset), download.Length, attempt.RunNumber);
    }
}

[OperationHandler]
public sealed class HeadCliHandler(IServiceProvider services, IConfiguration config) : Contract.ICliDownloadsHeadHandler
{
    public async Task<Contract.CliDownloadsHeadResponse> HandleAsync(Contract.CliDownloadsHeadRequest request, CancellationToken cancellationToken)
    {
        var asset = request.Asset;
        if (!CliDistribution.Assets.Contains(asset))
            return new Contract.CliDownloadsHeadResponse.NotFound();

        if (CliDistribution.LocalAsset(config, asset) is { } local)
            return new Contract.CliDownloadsHeadResponse.Ok(CliDistribution.Attachment(asset), new FileInfo(local).Length);
        if (CliDistribution.LocalBuildPending(config, asset))
            return new Contract.CliDownloadsHeadResponse.ServiceUnavailable(CliDistribution.RetryAfterSeconds);
        if (services.GetService<IAmazonS3>() is null)
            return new Contract.CliDownloadsHeadResponse.ServiceUnavailable();

        if (!services.GetRequiredService<CliReleaseQuery>().TryGetLatest(out var attempt)
            || await services.GetRequiredService<AttemptFileService>().GetSizeAsync(attempt, asset, cancellationToken) is not { } size)
            return new Contract.CliDownloadsHeadResponse.NotFound();
        return new Contract.CliDownloadsHeadResponse.Ok(CliDistribution.Attachment(asset), size, attempt.RunNumber);
    }
}

[OperationHandler]
public sealed class InstallShellHandler(IConfiguration config, IHttpContextAccessor http) : Contract.ICliInstallShellHandler
{
    public Task<Contract.CliInstallShellResponse> HandleAsync(Contract.CliInstallShellRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<Contract.CliInstallShellResponse>(CliDistribution.InstallScript(http.HttpContext?.Request, config, "install.sh") is { } script
            ? new Contract.CliInstallShellResponse.Ok(script)
            : new Contract.CliInstallShellResponse.NotFound());
}

[OperationHandler]
public sealed class InstallPowerShellHandler(IConfiguration config, IHttpContextAccessor http) : Contract.ICliInstallPowerShellHandler
{
    public Task<Contract.CliInstallPowerShellResponse> HandleAsync(Contract.CliInstallPowerShellRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<Contract.CliInstallPowerShellResponse>(CliDistribution.InstallScript(http.HttpContext?.Request, config, "install.ps1") is { } script
            ? new Contract.CliInstallPowerShellResponse.Ok(script)
            : new Contract.CliInstallPowerShellResponse.NotFound());
}
