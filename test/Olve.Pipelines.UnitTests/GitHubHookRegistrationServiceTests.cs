using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.GitHub;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Results;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests;

public class GitHubHookRegistrationServiceTests
{
    private const string BaseUrl = "https://pipelines-hooks.example.com";

    private sealed class FakeGitHubClient : IGitHubClient
    {
        public List<(string Owner, string Repo, string Pat, GitHubHookConfig Config)> CreateCalls { get; } = [];
        public List<(string Owner, string Repo, string Pat, long HookId)> DeleteCalls { get; } = [];
        public Result<long> CreateResult { get; set; } = 0L;
        public Result DeleteResult { get; set; } = Result.Success();

        public Task<Result<long>> CreateHookAsync(string owner, string repo, string pat, GitHubHookConfig config, CancellationToken ct = default)
        {
            CreateCalls.Add((owner, repo, pat, config));
            return Task.FromResult(CreateResult);
        }

        public Task<Result> DeleteHookAsync(string owner, string repo, string pat, long hookId, CancellationToken ct = default)
        {
            DeleteCalls.Add((owner, repo, pat, hookId));
            return Task.FromResult(DeleteResult);
        }
    }

    private sealed class FakeSecretReader(Result<string> result) : IPipelineSecretReader
    {
        public Task<Result<string>> TryGetSecretAsync(Id<Pipeline> pipelineId, string key, CancellationToken ct = default)
            => Task.FromResult(result);
    }

    private static GitHubHookRegistrationService CreateService(
        FakeGitHubClient gitHub, GitHubHookStateStore state, Result<string> pat)
        => new(
            new GitHubHookWorkQueue(),
            gitHub,
            state,
            new FakeSecretReader(pat),
            new WebhookOptions(BaseUrl),
            NullLogger<GitHubHookRegistrationService>.Instance);

    [Test]
    public async Task CreateWork_WithPat_CreatesHookAndStoresState()
    {
        var gitHub = new FakeGitHubClient { CreateResult = 4242L };
        var state = new GitHubHookStateStore();
        var service = CreateService(gitHub, state, "ghp_token");

        var triggerId = Id.New<Trigger>();
        var pipelineId = Id.New<Pipeline>();
        var work = new CreateHookWork(triggerId, pipelineId, "acme", "widgets", "GITHUB_TOKEN", "hook-secret");

        await service.ProcessAsync(work, CancellationToken.None);

        await Assert.That(gitHub.CreateCalls).Count().IsEqualTo(1);
        var call = gitHub.CreateCalls[0];
        await Assert.That(call.Owner).IsEqualTo("acme");
        await Assert.That(call.Repo).IsEqualTo("widgets");
        await Assert.That(call.Pat).IsEqualTo("ghp_token");
        await Assert.That(call.Config.Secret).IsEqualTo("hook-secret");
        await Assert.That(call.Config.Url).IsEqualTo($"{BaseUrl}/api/webhooks/github/{triggerId}");

        await Assert.That(state.TryGet(triggerId, out var stored)).IsTrue();
        await Assert.That(stored!.HookId).IsEqualTo(4242L);
        await Assert.That(stored.PipelineId).IsEqualTo(pipelineId);
        await Assert.That(stored.Owner).IsEqualTo("acme");
    }

    [Test]
    public async Task CreateWork_MissingPat_DoesNotCallGitHubOrStoreState()
    {
        var gitHub = new FakeGitHubClient { CreateResult = 1L };
        var state = new GitHubHookStateStore();
        var service = CreateService(gitHub, state, Result.Failure<string>(new ResultProblem("no secret")));

        var triggerId = Id.New<Trigger>();
        var work = new CreateHookWork(triggerId, Id.New<Pipeline>(), "acme", "widgets", "GITHUB_TOKEN", "hook-secret");

        await service.ProcessAsync(work, CancellationToken.None);

        await Assert.That(gitHub.CreateCalls).IsEmpty();
        await Assert.That(state.TryGet(triggerId, out _)).IsFalse();
    }

    [Test]
    public async Task CreateWork_AlreadyRegistered_IsNoOp()
    {
        var gitHub = new FakeGitHubClient { CreateResult = 1L };
        var state = new GitHubHookStateStore();
        var triggerId = Id.New<Trigger>();
        state.Set(triggerId, new GitHubHookState(Id.New<Pipeline>(), "acme", "widgets", 7L, "GITHUB_TOKEN"));
        var service = CreateService(gitHub, state, "ghp_token");

        var work = new CreateHookWork(triggerId, Id.New<Pipeline>(), "acme", "widgets", "GITHUB_TOKEN", "hook-secret");

        await service.ProcessAsync(work, CancellationToken.None);

        await Assert.That(gitHub.CreateCalls).IsEmpty();
    }

    [Test]
    public async Task DeleteWork_WithPat_DeletesHookWithStoredIdAndRemovesState()
    {
        var gitHub = new FakeGitHubClient();
        var state = new GitHubHookStateStore();
        var triggerId = Id.New<Trigger>();
        var pipelineId = Id.New<Pipeline>();
        state.Set(triggerId, new GitHubHookState(pipelineId, "acme", "widgets", 555L, "GITHUB_TOKEN"));
        var service = CreateService(gitHub, state, "ghp_token");

        var work = new DeleteHookWork(triggerId, pipelineId, "acme", "widgets", 555L, "GITHUB_TOKEN");

        await service.ProcessAsync(work, CancellationToken.None);

        await Assert.That(gitHub.DeleteCalls).Count().IsEqualTo(1);
        await Assert.That(gitHub.DeleteCalls[0].HookId).IsEqualTo(555L);
        await Assert.That(gitHub.DeleteCalls[0].Pat).IsEqualTo("ghp_token");
        await Assert.That(state.TryGet(triggerId, out _)).IsFalse();
    }

    [Test]
    public async Task DeleteWork_MissingPat_KeepsStateForRetry()
    {
        var gitHub = new FakeGitHubClient();
        var state = new GitHubHookStateStore();
        var triggerId = Id.New<Trigger>();
        var pipelineId = Id.New<Pipeline>();
        state.Set(triggerId, new GitHubHookState(pipelineId, "acme", "widgets", 555L, "GITHUB_TOKEN"));
        var service = CreateService(gitHub, state, Result.Failure<string>(new ResultProblem("no secret")));

        var work = new DeleteHookWork(triggerId, pipelineId, "acme", "widgets", 555L, "GITHUB_TOKEN");

        await service.ProcessAsync(work, CancellationToken.None);

        await Assert.That(gitHub.DeleteCalls).IsEmpty();
        await Assert.That(state.TryGet(triggerId, out _)).IsTrue();
    }

    [Test]
    public async Task DeleteWork_GitHubFails_KeepsStateForRetry()
    {
        var gitHub = new FakeGitHubClient { DeleteResult = new ResultProblem("boom") };
        var state = new GitHubHookStateStore();
        var triggerId = Id.New<Trigger>();
        var pipelineId = Id.New<Pipeline>();
        state.Set(triggerId, new GitHubHookState(pipelineId, "acme", "widgets", 555L, "GITHUB_TOKEN"));
        var service = CreateService(gitHub, state, "ghp_token");

        var work = new DeleteHookWork(triggerId, pipelineId, "acme", "widgets", 555L, "GITHUB_TOKEN");

        await service.ProcessAsync(work, CancellationToken.None);

        await Assert.That(gitHub.DeleteCalls).Count().IsEqualTo(1);
        await Assert.That(state.TryGet(triggerId, out _)).IsTrue();
    }
}
