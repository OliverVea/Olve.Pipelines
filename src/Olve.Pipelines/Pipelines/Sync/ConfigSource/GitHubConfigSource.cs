using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Olve.Pipelines.Kubernetes;

namespace Olve.Pipelines.Pipelines.Sync.ConfigSource;

/// <summary>
/// Reads pipeline config from GitHub. Pull-based and read-only: the deploy poll asks for the
/// branch head; reconcile fetches the config subtree with an ETag conditional request so idle
/// polls and code-only pushes cost nothing. The read token is resolved at fetch time from the
/// pipeline's own k8s secret via <see cref="PipelineConfigBinding.CredentialsSecret"/> (a
/// reference, never a raw value).
/// </summary>
public class GitHubConfigSource(
    IKubernetesClient kubernetes,
    KubernetesOptions kubernetesOptions,
    ILogger<GitHubConfigSource> logger) : IConfigSource
{
    private const string ApiBase = "https://api.github.com";

    public async Task<Result<string>> GetBranchHeadShaAsync(PipelineConfigBinding binding, CancellationToken ct = default)
    {
        var token = await GetTokenAsync(binding, ct);
        using var client = new HttpClient();

        using var request = BuildRequest(HttpMethod.Get, $"{ApiBase}/repos/{binding.Repo}/commits/{binding.Branch}", token);
        using var response = await client.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
            return new ResultProblem($"GitHub returned {(int)response.StatusCode} fetching branch head for '{binding.Repo}@{binding.Branch}'.");

        var commit = await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.GitHubCommitRef, ct);
        if (commit is null || string.IsNullOrEmpty(commit.Sha))
            return new ResultProblem($"GitHub returned no commit SHA for '{binding.Repo}@{binding.Branch}'.");

        return commit.Sha;
    }

    /// <summary>
    /// The config cursor is the newest commit touching the bound directory <b>or</b> one of the local
    /// path dependencies (<c>file:</c>/<c>link:</c>) of its <c>package.json</c> (see
    /// <see cref="LocalPathDependencies"/>). Each watched path costs one conditional request per poll
    /// (a free 304 when unchanged); <c>package.json</c> is read only when the bound directory's own
    /// cursor moved, since it lives there. The returned ETag is the encoded <see cref="ConfigCursorState"/>.
    /// </summary>
    public async Task<Result<ConfigFetch>> FetchConfigAsync(
        PipelineConfigBinding binding, string? etag = null, CancellationToken ct = default)
    {
        var token = await GetTokenAsync(binding, ct);
        using var client = CreateClient();
        var previous = ConfigCursorState.Decode(etag);

        // 1. The bound directory.
        var mainResult = await PathCursorAsync(client, binding, binding.Path, previous?.Paths[0], token, ct);
        if (mainResult.TryPickProblems(out var mainProblems, out var main))
            return mainProblems;
        if (main.Cursor is null)
            return new ResultProblem($"No commits touch '{binding.Path}' on '{binding.Repo}@{binding.Branch}'.");

        // 2. Its local path dependencies: re-read only when the directory changed.
        IReadOnlyList<string> dependencies;
        if (previous is not null && !main.Changed)
        {
            dependencies = previous.Paths.Skip(1).Select(p => p.Path).ToList();
        }
        else
        {
            var depsResult = await ReadLocalDependenciesAsync(client, binding, main.Cursor.Sha, token, ct);
            if (depsResult.TryPickProblems(out var depsProblems, out var resolved))
                return depsProblems;
            dependencies = resolved;
        }

        var cursors = new List<PathCursor> { main.Cursor };
        var changed = previous is null || main.Changed;
        foreach (var path in dependencies)
        {
            var depResult = await PathCursorAsync(client, binding, path, previous?.Find(path), token, ct);
            if (depResult.TryPickProblems(out var depProblems, out var dep))
                return depProblems;
            changed |= dep.Changed;
            if (dep.Cursor is not null)
                cursors.Add(dep.Cursor);
        }

        if (!changed)
            return new ConfigFetch.NotModified(etag!);

        var state = new ConfigCursorState(cursors);
        if (cursors.Count > 1)
            logger.LogDebug("Config cursor of '{Repo}@{Branch}' over {Paths}: {Sha}",
                binding.Repo, binding.Branch, string.Join(", ", cursors.Select(c => $"{c.Path}@{c.Sha[..Math.Min(12, c.Sha.Length)]}")), state.Sha);

        // v2 reconcile builds the pipeline from the repo tarball at this commit (build/describe Job), so the
        // subtree's blobs are not fetched here — the commit sha is all reconcile needs (and it keeps
        // unauthenticated bindings well inside GitHub's 60 requests/hour).
        return new ConfigFetch.Changed(state.Sha, state.Encode(), new Dictionary<string, string>(StringComparer.Ordinal));
    }

    /// <summary>HTTP client for GitHub; a seam for tests.</summary>
    protected virtual HttpClient CreateClient() => new();

    /// <param name="Cursor">Null when no commit touches the path.</param>
    /// <param name="Changed">False when GitHub answered 304 to <paramref name="Cursor"/>'s ETag.</param>
    private sealed record PathFetch(PathCursor? Cursor, bool Changed);

    /// <summary>Conditional request for the newest commit touching <paramref name="path"/> (<c>""</c> = any).</summary>
    private static async Task<Result<PathFetch>> PathCursorAsync(
        HttpClient client, PipelineConfigBinding binding, string path, PathCursor? previous, string? token, CancellationToken ct)
    {
        var pathQuery = path.Length > 0 ? $"path={Uri.EscapeDataString(path)}&" : "";
        var url = $"{ApiBase}/repos/{binding.Repo}/commits?{pathQuery}sha={Uri.EscapeDataString(binding.Branch)}&per_page=1";
        using var request = BuildRequest(HttpMethod.Get, url, token);
        if (previous is { ETag.Length: > 0 })
            request.Headers.IfNoneMatch.ParseAdd(previous.ETag);

        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotModified && previous is not null)
            return new PathFetch(previous, Changed: false);
        if (!response.IsSuccessStatusCode)
            return new ResultProblem($"GitHub returned {(int)response.StatusCode} fetching config cursor for '{binding.Repo}' ({(path.Length > 0 ? path : "/")}).");

        var commits = await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.GitHubCommitListItemArray, ct);
        if (commits is not { Length: > 0 })
            return new PathFetch(null, Changed: previous is not null);

        var newEtag = response.Headers.ETag?.ToString() ?? string.Empty;
        var date = commits[0].Commit?.Committer?.Date ?? DateTimeOffset.MinValue;
        return new PathFetch(new PathCursor(path, newEtag, commits[0].Sha, date), Changed: true);
    }

    /// <summary>The bound directory's local path dependencies at <paramref name="sha"/> (none without a package.json).</summary>
    private async Task<Result<IReadOnlyList<string>>> ReadLocalDependenciesAsync(
        HttpClient client, PipelineConfigBinding binding, string sha, string? token, CancellationToken ct)
    {
        var dir = LocalPathDependencies.Normalize(binding.Path) ?? binding.Path;
        var file = dir.Length > 0 ? $"{dir}/package.json" : "package.json";
        var url = $"{ApiBase}/repos/{binding.Repo}/contents/{string.Join('/', file.Split('/').Select(Uri.EscapeDataString))}?ref={Uri.EscapeDataString(sha)}";
        using var request = BuildRequest(HttpMethod.Get, url, token);
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("application/vnd.github.raw+json");

        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return Result.Success<IReadOnlyList<string>>([]);
        if (!response.IsSuccessStatusCode)
            return new ResultProblem($"GitHub returned {(int)response.StatusCode} fetching '{file}' of '{binding.Repo}'.");

        var dependencies = LocalPathDependencies.Resolve(binding.Path, await response.Content.ReadAsStringAsync(ct));
        if (dependencies.Count > 0)
            logger.LogInformation("Config of '{Repo}@{Branch}' ({Path}) also watches its local path dependencies {Dependencies}",
                binding.Repo, binding.Branch, binding.Path, string.Join(", ", dependencies.Select(d => d.Length > 0 ? d : "/")));
        return Result.Success(dependencies);
    }

    private static HttpRequestMessage BuildRequest(HttpMethod method, string url, string? token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.UserAgent.ParseAdd("olve-pipelines");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return request;
    }

    private async Task<string?> GetTokenAsync(PipelineConfigBinding binding, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(binding.CredentialsSecret))
            return null;

        try
        {
            var secretName = $"olve-pipeline-{binding.PipelineId.Value.Value:N}";
            var secrets = await kubernetes.GetSecretAsync(kubernetesOptions.Namespace, secretName, ct);
            if (secrets is not null && secrets.TryGetValue(binding.CredentialsSecret, out var token))
                return token;

            logger.LogWarning(
                "Credentials secret key '{Key}' not found for pipeline '{PipelineId}'", binding.CredentialsSecret, binding.PipelineId);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve credentials for pipeline '{PipelineId}'", binding.PipelineId);
            return null;
        }
    }
}
