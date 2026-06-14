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
/// reference, never a raw value) — mirroring the <c>$SECRET</c> resolution in PollTriggerService.
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

    public async Task<Result<ConfigFetch>> FetchConfigAsync(
        PipelineConfigBinding binding, string? etag = null, CancellationToken ct = default)
    {
        var token = await GetTokenAsync(binding, ct);
        using var client = new HttpClient();

        // 1. Conditional cursor request — the head commit touching the config path.
        var cursorUrl = $"{ApiBase}/repos/{binding.Repo}/commits?path={Uri.EscapeDataString(binding.Path)}&sha={Uri.EscapeDataString(binding.Branch)}&per_page=1";
        using var cursorRequest = BuildRequest(HttpMethod.Get, cursorUrl, token);
        if (!string.IsNullOrEmpty(etag))
            cursorRequest.Headers.IfNoneMatch.ParseAdd(etag);

        using var cursorResponse = await client.SendAsync(cursorRequest, ct);

        if (cursorResponse.StatusCode == HttpStatusCode.NotModified)
            return new ConfigFetch.NotModified(etag!);

        if (!cursorResponse.IsSuccessStatusCode)
            return new ResultProblem($"GitHub returned {(int)cursorResponse.StatusCode} fetching config cursor for '{binding.Repo}'.");

        var commits = await cursorResponse.Content.ReadFromJsonAsync(GitHubJsonContext.Default.GitHubCommitListItemArray, ct);
        if (commits is not { Length: > 0 })
            return new ResultProblem($"No commits touch '{binding.Path}' on '{binding.Repo}@{binding.Branch}'.");

        var sha = commits[0].Sha;
        var newEtag = cursorResponse.Headers.ETag?.ToString() ?? string.Empty;

        // 2. Fetch the tree at that commit and pull the blobs under the config path.
        var filesResult = await FetchFilesAsync(client, binding, sha, token, ct);
        if (filesResult.TryPickProblems(out var problems, out var files))
            return problems;

        return new ConfigFetch.Changed(sha, newEtag, files);
    }

    private async Task<Result<IReadOnlyDictionary<string, string>>> FetchFilesAsync(
        HttpClient client, PipelineConfigBinding binding, string commitSha, string? token, CancellationToken ct)
    {
        using var treeRequest = BuildRequest(HttpMethod.Get, $"{ApiBase}/repos/{binding.Repo}/git/trees/{commitSha}?recursive=1", token);
        using var treeResponse = await client.SendAsync(treeRequest, ct);

        if (!treeResponse.IsSuccessStatusCode)
            return new ResultProblem($"GitHub returned {(int)treeResponse.StatusCode} fetching tree '{commitSha}' for '{binding.Repo}'.");

        var tree = await treeResponse.Content.ReadFromJsonAsync(GitHubJsonContext.Default.GitHubTree, ct);
        if (tree is null)
            return new ResultProblem($"GitHub returned an empty tree '{commitSha}' for '{binding.Repo}'.");

        if (tree.Truncated)
            logger.LogWarning("GitHub tree '{Sha}' for '{Repo}' was truncated; config may be incomplete", commitSha, binding.Repo);

        var prefix = binding.Path.TrimEnd('/') + "/";
        var files = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in tree.Tree)
        {
            if (entry.Type != "blob" || !entry.Path.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            using var blobRequest = BuildRequest(HttpMethod.Get, $"{ApiBase}/repos/{binding.Repo}/git/blobs/{entry.Sha}", token);
            using var blobResponse = await client.SendAsync(blobRequest, ct);

            if (!blobResponse.IsSuccessStatusCode)
                return new ResultProblem($"GitHub returned {(int)blobResponse.StatusCode} fetching blob '{entry.Path}' for '{binding.Repo}'.");

            var blob = await blobResponse.Content.ReadFromJsonAsync(GitHubJsonContext.Default.GitHubBlob, ct);
            if (blob is null)
                return new ResultProblem($"GitHub returned an empty blob for '{entry.Path}'.");

            var content = blob.Encoding == "base64"
                ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(blob.Content.Replace("\n", string.Empty)))
                : blob.Content;

            files[entry.Path[prefix.Length..]] = content;
        }

        return files;
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
