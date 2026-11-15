using System.Net;
using System.Net.Http.Json;
using Olve.Pipelines.GitHub;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Sync.ConfigSource;

namespace Olve.Pipelines.Pipelines.Ci;

/// <summary>Reads a binding's pull requests from GitHub (tests use a fake).</summary>
public interface IPullRequestSource
{
    /// <summary>The open pull requests into the binding's branch.</summary>
    Task<Result<IReadOnlyList<GitHubPullRequest>>> ListOpenAsync(PipelineConfigBinding binding, CancellationToken ct = default);

    /// <summary>One pull request of the binding's repository; null when GitHub has none by that number.</summary>
    Task<Result<GitHubPullRequest?>> GetAsync(PipelineConfigBinding binding, int number, CancellationToken ct = default);
}

public class GitHubPullRequestSource(
    IKubernetesClient kubernetes,
    KubernetesOptions kubernetesOptions,
    IConfiguration configuration,
    ILogger<GitHubPullRequestSource> logger) : IPullRequestSource
{
    private const int PageSize = 100;
    private const int MaxPages = 10;

    private readonly string _apiBase = GitHubEndpoints.ApiUrl(configuration);

    public async Task<Result<IReadOnlyList<GitHubPullRequest>>> ListOpenAsync(PipelineConfigBinding binding, CancellationToken ct = default)
    {
        var token = await BindingTokens.GetAsync(kubernetes, kubernetesOptions, binding, logger, ct);
        using var client = new HttpClient();
        var pulls = new List<GitHubPullRequest>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var url = $"{_apiBase}/repos/{binding.Repo}/pulls?state=open&base={Uri.EscapeDataString(binding.Branch)}&per_page={PageSize}&page={page}";
            using var request = GitHubConfigSource.BuildRequest(HttpMethod.Get, url, token);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return new ResultProblem("GitHub returned {0} listing the open pull requests of '{1}' into '{2}'.", (int)response.StatusCode, binding.Repo, binding.Branch);

            var listed = await response.Content.ReadFromJsonAsync(GitHubPullRequestJsonContext.Default.GitHubPullRequestArray, ct) ?? [];
            pulls.AddRange(listed);
            if (listed.Length < PageSize)
                break;
        }

        return pulls;
    }

    public async Task<Result<GitHubPullRequest?>> GetAsync(PipelineConfigBinding binding, int number, CancellationToken ct = default)
    {
        var token = await BindingTokens.GetAsync(kubernetes, kubernetesOptions, binding, logger, ct);
        using var client = new HttpClient();
        using var request = GitHubConfigSource.BuildRequest(HttpMethod.Get, $"{_apiBase}/repos/{binding.Repo}/pulls/{number}", token);
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return Result.Success<GitHubPullRequest?>(null);
        if (!response.IsSuccessStatusCode)
            return new ResultProblem("GitHub returned {0} reading pull request #{1} of '{2}'.", (int)response.StatusCode, number, binding.Repo);

        return await response.Content.ReadFromJsonAsync(GitHubPullRequestJsonContext.Default.GitHubPullRequest, ct);
    }
}
