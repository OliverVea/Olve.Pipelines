using System.Formats.Tar;
using System.IO.Compression;

namespace Olve.Pipelines.Cli;

/// <summary>
/// Resolves the helm chart for <c>pl bootstrap</c> — either a local directory (<c>--chart</c>)
/// or, by default, the chart pulled from the GitHub repo tarball at a pinned ref. Pulling
/// keeps <c>pl</c> self-contained (no repo checkout needed) for disaster recovery.
/// </summary>
public sealed class ChartFetcher
{
    public const string DefaultRepo = "OliverVea/Olve.Pipelines";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    /// <summary>Path to a usable helm chart directory, plus a temp root to clean up (or null).</summary>
    public sealed record ChartLocation(string ChartDirectory, string? TempRoot);

    public async Task<Result<ChartLocation>> ResolveAsync(
        string? localChartPath, string repo, string gitRef, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(localChartPath))
        {
            var full = Path.GetFullPath(localChartPath);
            if (!Directory.Exists(full))
                return new ResultProblem("Chart directory '{0}' does not exist.", full);
            if (!File.Exists(Path.Combine(full, "Chart.yaml")))
                return new ResultProblem("'{0}' is not a helm chart (no Chart.yaml).", full);
            return new ChartLocation(full, null);
        }

        var url = $"https://github.com/{repo}/archive/{gitRef}.tar.gz";
        var tempRoot = Directory.CreateTempSubdirectory("pl-chart-").FullName;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("olve-pipelines-pl-cli");

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                Directory.Delete(tempRoot, recursive: true);
                return new ResultProblem(
                    "Failed to download chart from {0}: HTTP {1}. Check the --ref exists on '{2}'.",
                    url, (int)response.StatusCode, repo);
            }

            await using var netStream = await response.Content.ReadAsStreamAsync(ct);
            await using var gzip = new GZipStream(netStream, CompressionMode.Decompress);
            await TarFile.ExtractToDirectoryAsync(gzip, tempRoot, overwriteFiles: true, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            TryDelete(tempRoot);
            return new ResultProblem("Failed to fetch chart from {0}: {1}", url, ex.Message);
        }

        // The archive unpacks to a single top-level dir (e.g. Olve.Pipelines-<sha>/); the chart is its helm/.
        var chartDir = Directory.EnumerateDirectories(tempRoot)
            .Select(d => Path.Combine(d, "helm"))
            .FirstOrDefault(Directory.Exists);

        if (chartDir is null)
        {
            TryDelete(tempRoot);
            return new ResultProblem("Downloaded archive from {0} did not contain a helm/ chart directory.", url);
        }

        return new ChartLocation(chartDir, tempRoot);
    }

    public static void TryDelete(string? path)
    {
        if (path is null) return;
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }
}
