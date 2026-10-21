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

        // v2 reconcile builds the pipeline from the repo tarball at this commit (build/describe Job), so the
        // subtree's blobs are not fetched here — the commit sha is all reconcile needs (and it keeps
        // unauthenticated bindings well inside GitHub's 60 requests/hour).
        return new ConfigFetch.Changed(sha, newEtag, new Dictionary<string, string>(StringComparer.Ordinal));
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
