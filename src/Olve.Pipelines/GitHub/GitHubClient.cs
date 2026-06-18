using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Olve.Pipelines.GitHub;

/// <summary>The hook settings we register on a repo: the delivery URL and the HMAC secret.</summary>
public record GitHubHookConfig(string Url, string Secret);

/// <summary>
/// Manages repository <c>push</c> webhooks via the GitHub REST API. The personal access token is a
/// per-call argument (each pipeline carries its own fine-grained PAT), so a single shared client
/// serves every pipeline — the token is attached per request rather than baked into the client.
/// </summary>
public interface IGitHubClient
{
    /// <summary>Creates a <c>push</c> webhook on the repo and returns its hook id.</summary>
    Task<Result<long>> CreateHookAsync(string owner, string repo, string pat, GitHubHookConfig config, CancellationToken ct = default);

    /// <summary>Deletes the hook. A missing hook (404) is treated as success — the end state is the same.</summary>
    Task<Result> DeleteHookAsync(string owner, string repo, string pat, long hookId, CancellationToken ct = default);
}

public class GitHubClient : IGitHubClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GitHubClient>? _logger;

    public GitHubClient(ILogger<GitHubClient>? logger = null)
    {
        _logger = logger;
        _httpClient = new HttpClient { BaseAddress = new Uri("https://api.github.com/") };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("olve-pipelines");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<Result<long>> CreateHookAsync(
        string owner, string repo, string pat, GitHubHookConfig config, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"repos/{owner}/{repo}/hooks")
        {
            Content = JsonContent.Create(
                new CreateHookRequest(
                    "web", true, ["push"],
                    new CreateHookConfig(config.Url, "json", config.Secret, "0")),
                GitHubApiJsonContext.Default.CreateHookRequest),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pat);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "GitHub create-hook request failed for {Owner}/{Repo}", owner, repo);
            return Result.Failure<long>(new ResultProblem($"GitHub create-hook request failed for '{owner}/{repo}': {ex.Message}"));
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger?.LogError("GitHub create-hook for {Owner}/{Repo} returned {StatusCode}: {Body}", owner, repo, response.StatusCode, body);
            return Result.Failure<long>(new ResultProblem($"GitHub create-hook for '{owner}/{repo}' returned {(int)response.StatusCode}."));
        }

        var created = await response.Content.ReadFromJsonAsync(GitHubApiJsonContext.Default.CreateHookResponse, ct);
        if (created is null || created.Id == 0)
            return Result.Failure<long>(new ResultProblem($"GitHub create-hook for '{owner}/{repo}' returned no hook id."));

        _logger?.LogInformation("Created GitHub push hook {HookId} on {Owner}/{Repo}", created.Id, owner, repo);
        return created.Id;
    }

    public async Task<Result> DeleteHookAsync(
        string owner, string repo, string pat, long hookId, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"repos/{owner}/{repo}/hooks/{hookId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pat);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "GitHub delete-hook request failed for {Owner}/{Repo} hook {HookId}", owner, repo, hookId);
            return new ResultProblem($"GitHub delete-hook request failed for '{owner}/{repo}' hook {hookId}: {ex.Message}");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger?.LogInformation("GitHub hook {HookId} on {Owner}/{Repo} already gone (404) — treating delete as done", hookId, owner, repo);
            return Result.Success();
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger?.LogError("GitHub delete-hook for {Owner}/{Repo} hook {HookId} returned {StatusCode}: {Body}", owner, repo, hookId, response.StatusCode, body);
            return new ResultProblem($"GitHub delete-hook for '{owner}/{repo}' hook {hookId} returned {(int)response.StatusCode}.");
        }

        _logger?.LogInformation("Deleted GitHub push hook {HookId} on {Owner}/{Repo}", hookId, owner, repo);
        return Result.Success();
    }

    public void Dispose() => _httpClient.Dispose();
}

internal record CreateHookRequest(string Name, bool Active, string[] Events, CreateHookConfig Config);

internal record CreateHookConfig(
    string Url,
    [property: JsonPropertyName("content_type")] string ContentType,
    string Secret,
    [property: JsonPropertyName("insecure_ssl")] string InsecureSsl);

internal record CreateHookResponse(long Id);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CreateHookRequest))]
[JsonSerializable(typeof(CreateHookResponse))]
internal partial class GitHubApiJsonContext : JsonSerializerContext;
