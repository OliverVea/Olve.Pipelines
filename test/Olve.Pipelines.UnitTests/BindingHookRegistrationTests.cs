using Olve.Pipelines.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.GitHub;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Shared;
using Olve.Results;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests;

public class BindingHookRegistrationTests
{
    private const string BaseUrl = "https://pipelines-hooks.example.com";

    private sealed class FakeGitHubClient : IGitHubClient
    {
        public List<(string Owner, string Repo, string Pat, GitHubHookConfig Config)> CreateCalls { get; } = [];
        public List<long> DeletedHookIds { get; } = [];
        public Result<long> CreateResult { get; set; } = 0L;

        public Task<Result<long>> CreateHookAsync(string owner, string repo, string pat, GitHubHookConfig config, CancellationToken ct = default)
        {
            CreateCalls.Add((owner, repo, pat, config));
            return Task.FromResult(CreateResult);
        }

        public Task<Result> DeleteHookAsync(string owner, string repo, string pat, long hookId, CancellationToken ct = default)
        {
            DeletedHookIds.Add(hookId);
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class FakeSecretReader(Result<string> result) : IPipelineSecretReader
    {
        public Task<Result<string>> TryGetSecretAsync(Id<Pipeline> pipelineId, string key, CancellationToken ct = default)
            => Task.FromResult(result);
    }

    // ---- drainer ----

    private static BindingHookRegistrationService CreateDrainer(FakeGitHubClient gh, BindingHookStateStore state, Result<string> pat)
        => new(new BindingHookWorkQueue(), gh, state, new FakeSecretReader(pat), new WebhookOptions(BaseUrl),
            NullLogger<BindingHookRegistrationService>.Instance);

    [Test]
    public async Task Create_WithPat_RegistersHookAndStoresState()
    {
        var gh = new FakeGitHubClient { CreateResult = 321L };
        var state = new BindingHookStateStore();
        var drainer = CreateDrainer(gh, state, "ghp_tok");
        var bindingId = Id.New<PipelineConfigBinding>();
        var pipelineId = Id.New<Pipeline>();

        await drainer.ProcessAsync(new CreateBindingHookWork(bindingId, pipelineId, "acme", "widgets", "GITHUB_TOKEN", "hook-secret"), CancellationToken.None);

        await Assert.That(gh.CreateCalls).Count().IsEqualTo(1);
        await Assert.That(gh.CreateCalls[0].Config.Url).IsEqualTo($"{BaseUrl}/api/webhooks/binding/{bindingId}/github");
        await Assert.That(gh.CreateCalls[0].Config.Secret).IsEqualTo("hook-secret");
        await Assert.That(state.TryGet(bindingId, out var stored)).IsTrue();
        await Assert.That(stored!.HookId).IsEqualTo(321L);
    }

    [Test]
    public async Task Create_MissingPat_NoCallNoState()
    {
        var gh = new FakeGitHubClient { CreateResult = 1L };
        var state = new BindingHookStateStore();
        var drainer = CreateDrainer(gh, state, Result.Failure<string>(new ResultProblem("no secret")));
        var bindingId = Id.New<PipelineConfigBinding>();

        await drainer.ProcessAsync(new CreateBindingHookWork(bindingId, Id.New<Pipeline>(), "acme", "widgets", "GITHUB_TOKEN", "s"), CancellationToken.None);

        await Assert.That(gh.CreateCalls).IsEmpty();
        await Assert.That(state.TryGet(bindingId, out _)).IsFalse();
    }

    [Test]
    public async Task Delete_WithPat_DeletesAndRemovesState()
    {
        var gh = new FakeGitHubClient();
        var state = new BindingHookStateStore();
        var bindingId = Id.New<PipelineConfigBinding>();
        var pipelineId = Id.New<Pipeline>();
        state.Set(bindingId, new BindingHookState(pipelineId, "acme", "widgets", 77L, "GITHUB_TOKEN"));
        var drainer = CreateDrainer(gh, state, "ghp_tok");

        await drainer.ProcessAsync(new DeleteBindingHookWork(bindingId, pipelineId, "acme", "widgets", 77L, "GITHUB_TOKEN"), CancellationToken.None);

        await Assert.That(gh.DeletedHookIds).Contains(77L);
        await Assert.That(state.TryGet(bindingId, out _)).IsFalse();
    }

    // ---- event registration ----

    private record Harness(EntityStore<PipelineConfigBinding> Store, PipelineConfigBindingService Svc, BindingHookStateStore HookState, BindingHookWorkQueue Queue);

    private static Harness CreateEventHarness(string? baseUrl = BaseUrl)
    {
        var store = new EntityStore<PipelineConfigBinding>([]);
        var svc = new PipelineConfigBindingService(store, new IdProvider());
        var hookState = new BindingHookStateStore();
        var queue = new BindingHookWorkQueue();
        var reg = new BindingWebhookEventRegistration(store, hookState, queue, new WebhookOptions(baseUrl), NullLogger<BindingWebhookEventRegistration>.Instance);
        reg.Run();
        return new Harness(store, svc, hookState, queue);
    }

    private static T Pick<T>(Result<T> r) { r.TryPickProblems(out _, out var v); return v!; }

    [Test]
    public async Task WebhookBindingCreated_EnqueuesCreate()
    {
        var h = CreateEventHarness();
        var binding = Pick(h.Svc.Create(Id.New<Pipeline>(), "acme/widgets", "main", ".pipelines", "GITHUB_TOKEN"));

        await Assert.That(h.Queue.Reader.TryRead(out var work)).IsTrue();
        var create = work as CreateBindingHookWork;
        await Assert.That(create).IsNotNull();
        await Assert.That(create!.BindingId).IsEqualTo(binding.Id);
        await Assert.That(create.Owner).IsEqualTo("acme");
        await Assert.That(create.Repo).IsEqualTo("widgets");
    }

    [Test]
    public async Task PollBindingCreated_EnqueuesNothing()
    {
        var h = CreateEventHarness();
        Pick(h.Svc.Create(Id.New<Pipeline>(), "acme/widgets", "main", ".pipelines", "GITHUB_TOKEN", BindingDeployTrigger.Poll));

        await Assert.That(h.Queue.Reader.TryRead(out _)).IsFalse();
    }

    [Test]
    public async Task WebhookBindingCreated_NoPublicUrl_EnqueuesNothing()
    {
        var h = CreateEventHarness(baseUrl: null);
        Pick(h.Svc.Create(Id.New<Pipeline>(), "acme/widgets", "main", ".pipelines", "GITHUB_TOKEN"));

        await Assert.That(h.Queue.Reader.TryRead(out _)).IsFalse();
    }

    [Test]
    public async Task WebhookBindingCreated_NoCredentials_EnqueuesNothing()
    {
        // Without a credentials secret there is no PAT to manage the hook → fall back to polling.
        var h = CreateEventHarness();
        Pick(h.Svc.Create(Id.New<Pipeline>(), "acme/widgets", "main", ".pipelines", credentialsSecret: null));

        await Assert.That(h.Queue.Reader.TryRead(out _)).IsFalse();
    }

    [Test]
    public async Task SwitchedToPoll_WithLiveHook_EnqueuesDelete()
    {
        var h = CreateEventHarness();
        var pid = Id.New<Pipeline>();
        var binding = Pick(h.Svc.Create(pid, "acme/widgets", "main", ".pipelines", "GITHUB_TOKEN"));
        h.Queue.Reader.TryRead(out _); // drain the create from above
        // Simulate the hook having been registered.
        h.HookState.Set(binding.Id, new BindingHookState(pid, "acme", "widgets", 9L, "GITHUB_TOKEN"));

        Pick(h.Svc.SetDeployTrigger(pid, BindingDeployTrigger.Poll));

        await Assert.That(h.Queue.Reader.TryRead(out var work)).IsTrue();
        var delete = work as DeleteBindingHookWork;
        await Assert.That(delete).IsNotNull();
        await Assert.That(delete!.HookId).IsEqualTo(9L);
    }

    [Test]
    public async Task BindingDeleted_WithLiveHook_EnqueuesDelete()
    {
        var h = CreateEventHarness();
        var pid = Id.New<Pipeline>();
        var binding = Pick(h.Svc.Create(pid, "acme/widgets", "main", ".pipelines", "GITHUB_TOKEN"));
        h.Queue.Reader.TryRead(out _); // drain create
        h.HookState.Set(binding.Id, new BindingHookState(pid, "acme", "widgets", 5L, "GITHUB_TOKEN"));

        h.Svc.Delete(binding.Id);

        await Assert.That(h.Queue.Reader.TryRead(out var work)).IsTrue();
        await Assert.That(work as DeleteBindingHookWork).IsNotNull();
    }
}
