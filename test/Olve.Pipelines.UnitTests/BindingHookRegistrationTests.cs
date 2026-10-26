using Olve.Pipelines.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.GitHub;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Shared;
using Olve.Results;
using Olve.Results.TUnit;
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

        public List<(long HookId, GitHubHookConfig Config)> UpdateCalls { get; } = [];
        public Result UpdateResult { get; set; } = Result.Success();

        public Task<Result> UpdateHookConfigAsync(string owner, string repo, string pat, long hookId, GitHubHookConfig config, CancellationToken ct = default)
        {
            UpdateCalls.Add((hookId, config));
            return Task.FromResult(UpdateResult);
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

    private static string UrlOf(Id<PipelineConfigBinding> bindingId) => $"{BaseUrl}/api/webhooks/binding/{bindingId}/github";

    [Test]
    public async Task Create_RegisteredAtTheCurrentUrl_DoesNothing()
    {
        var gh = new FakeGitHubClient();
        var state = new BindingHookStateStore();
        var bindingId = Id.New<PipelineConfigBinding>();
        var pipelineId = Id.New<Pipeline>();
        state.Set(bindingId, new BindingHookState(pipelineId, "acme", "widgets", 7L, "GITHUB_TOKEN", UrlOf(bindingId)));

        await CreateDrainer(gh, state, "ghp_tok").ProcessAsync(new CreateBindingHookWork(bindingId, pipelineId, "acme", "widgets", "GITHUB_TOKEN", "s"), CancellationToken.None);

        await Assert.That(gh.CreateCalls).IsEmpty();
        await Assert.That(gh.UpdateCalls).IsEmpty();
    }

    [Test]
    [Arguments("https://old-hooks.example.com/api/webhooks/binding/x/github")]
    [Arguments(null)]
    public async Task Create_RegisteredElsewhere_RepointsTheSameHook(string? oldUrl)
    {
        var gh = new FakeGitHubClient();
        var state = new BindingHookStateStore();
        var bindingId = Id.New<PipelineConfigBinding>();
        var pipelineId = Id.New<Pipeline>();
        state.Set(bindingId, new BindingHookState(pipelineId, "acme", "widgets", 7L, "GITHUB_TOKEN", oldUrl));

        await CreateDrainer(gh, state, "ghp_tok").ProcessAsync(new CreateBindingHookWork(bindingId, pipelineId, "acme", "widgets", "GITHUB_TOKEN", "hook-secret"), CancellationToken.None);

        await Assert.That(gh.CreateCalls).IsEmpty();
        await Assert.That(gh.UpdateCalls).Count().IsEqualTo(1);
        await Assert.That(gh.UpdateCalls[0].HookId).IsEqualTo(7L);
        await Assert.That(gh.UpdateCalls[0].Config).IsEqualTo(new GitHubHookConfig(UrlOf(bindingId), "hook-secret"));
        await Assert.That(state.TryGet(bindingId, out var stored)).IsTrue();
        await Assert.That(stored!.Url).IsEqualTo(UrlOf(bindingId));
    }

    [Test]
    public async Task Create_RegisteredHookGoneOnGitHub_RegistersANewOne()
    {
        var gh = new FakeGitHubClient { CreateResult = 99L, UpdateResult = new NotFoundProblem("gone") };
        var state = new BindingHookStateStore();
        var bindingId = Id.New<PipelineConfigBinding>();
        var pipelineId = Id.New<Pipeline>();
        state.Set(bindingId, new BindingHookState(pipelineId, "acme", "widgets", 7L, "GITHUB_TOKEN", "https://old.example.com/x"));

        await CreateDrainer(gh, state, "ghp_tok").ProcessAsync(new CreateBindingHookWork(bindingId, pipelineId, "acme", "widgets", "GITHUB_TOKEN", "s"), CancellationToken.None);

        await Assert.That(gh.CreateCalls).Count().IsEqualTo(1);
        await Assert.That(state.TryGet(bindingId, out var stored)).IsTrue();
        await Assert.That(stored!.HookId).IsEqualTo(99L);
        await Assert.That(stored.Url).IsEqualTo(UrlOf(bindingId));
    }

    [Test]
    public async Task Create_RepointFailsOtherwise_KeepsTheOldState()
    {
        var gh = new FakeGitHubClient { UpdateResult = new ResultProblem("rate limited") };
        var state = new BindingHookStateStore();
        var bindingId = Id.New<PipelineConfigBinding>();
        var pipelineId = Id.New<Pipeline>();
        state.Set(bindingId, new BindingHookState(pipelineId, "acme", "widgets", 7L, "GITHUB_TOKEN", "https://old.example.com/x"));

        await CreateDrainer(gh, state, "ghp_tok").ProcessAsync(new CreateBindingHookWork(bindingId, pipelineId, "acme", "widgets", "GITHUB_TOKEN", "s"), CancellationToken.None);

        await Assert.That(gh.CreateCalls).IsEmpty();
        await Assert.That(state.TryGet(bindingId, out var stored)).IsTrue();
        await Assert.That(stored!.Url).IsEqualTo("https://old.example.com/x");
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

    private static async Task<Harness> CreateEventHarness(string? baseUrl = BaseUrl)
    {
        var store = new EntityStore<PipelineConfigBinding>([]);
        var svc = new PipelineConfigBindingService(store, new IdProvider());
        var hookState = new BindingHookStateStore();
        var queue = new BindingHookWorkQueue();
        var reg = new BindingWebhookEventRegistration(store, hookState, queue, new WebhookOptions(baseUrl), new IdProvider(), NullLogger<BindingWebhookEventRegistration>.Instance);
        await Assert.That(reg.Run()).Succeeded();
        return new Harness(store, svc, hookState, queue);
    }

    private static T Pick<T>(Result<T> r) { r.TryPickProblems(out _, out var v); return v!; }

    [Test]
    public async Task BindingCreated_EnqueuesCreate()
    {
        var h = await CreateEventHarness();
        var binding = Pick(h.Svc.Create(Id.New<Pipeline>(), "acme/widgets", "main", ".pipelines", "GITHUB_TOKEN"));

        await Assert.That(h.Queue.Reader.TryRead(out var work)).IsTrue();
        var create = work as CreateBindingHookWork;
        await Assert.That(create).IsNotNull();
        await Assert.That(create!.BindingId).IsEqualTo(binding.Id);
        await Assert.That(create.Owner).IsEqualTo("acme");
        await Assert.That(create.Repo).IsEqualTo("widgets");
    }

    [Test]
    public async Task BindingWithoutSecret_GetsOne_AndEnqueuesCreate()
    {
        var h = await CreateEventHarness();
        var binding = new PipelineConfigBinding(Id.New<PipelineConfigBinding>(), Id.New<Pipeline>(), "acme/widgets", "main", ".pipelines",
            "GITHUB_TOKEN", null, null, ReconcileStatus.NeverRun, DateTimeOffset.UtcNow, WebhookSecret: null);

        h.Store.Set(binding);

        await Assert.That(h.Store.TryGet(binding.Id, out var stored)).IsTrue();
        await Assert.That(string.IsNullOrEmpty(stored!.WebhookSecret)).IsFalse();
        await Assert.That(h.Queue.Reader.TryRead(out var work)).IsTrue();
        await Assert.That(work).IsTypeOf<CreateBindingHookWork>();
    }

    [Test]
    public async Task BindingCreated_NoPublicUrl_EnqueuesNothing()
    {
        var h = await CreateEventHarness(baseUrl: null);
        Pick(h.Svc.Create(Id.New<Pipeline>(), "acme/widgets", "main", ".pipelines", "GITHUB_TOKEN"));

        await Assert.That(h.Queue.Reader.TryRead(out _)).IsFalse();
    }

    [Test]
    public async Task BindingCreated_NoCredentials_EnqueuesNothing()
    {
        // Without a credentials secret there is no PAT to manage the hook → fall back to polling.
        var h = await CreateEventHarness();
        Pick(h.Svc.Create(Id.New<Pipeline>(), "acme/widgets", "main", ".pipelines", credentialsSecret: null));

        await Assert.That(h.Queue.Reader.TryRead(out _)).IsFalse();
    }

    [Test]
    public async Task CredentialsCleared_WithLiveHook_EnqueuesDelete()
    {
        var h = await CreateEventHarness();
        var pid = Id.New<Pipeline>();
        var binding = Pick(h.Svc.Create(pid, "acme/widgets", "main", ".pipelines", "GITHUB_TOKEN"));
        h.Queue.Reader.TryRead(out _); // drain the create from above
        // Simulate the hook having been registered.
        h.HookState.Set(binding.Id, new BindingHookState(pid, "acme", "widgets", 9L, "GITHUB_TOKEN"));

        Pick(h.Svc.SetCredentialsSecret(pid, null));

        await Assert.That(h.Queue.Reader.TryRead(out var work)).IsTrue();
        var delete = work as DeleteBindingHookWork;
        await Assert.That(delete).IsNotNull();
        await Assert.That(delete!.HookId).IsEqualTo(9L);
    }

    [Test]
    public async Task BindingUpdated_HookAtAnOldUrl_EnqueuesARepoint()
    {
        var h = await CreateEventHarness();
        var pid = Id.New<Pipeline>();
        var binding = Pick(h.Svc.Create(pid, "acme/widgets", "main", ".pipelines", "GITHUB_TOKEN"));
        h.Queue.Reader.TryRead(out _);
        h.HookState.Set(binding.Id, new BindingHookState(pid, "acme", "widgets", 5L, "GITHUB_TOKEN", "https://old.example.com/x"));

        Pick(h.Svc.SetLastPushSha(binding.Id, "abc"));

        await Assert.That(h.Queue.Reader.TryRead(out var work)).IsTrue();
        await Assert.That(work as CreateBindingHookWork).IsNotNull();
    }

    [Test]
    public async Task BindingUpdated_HookAtTheCurrentUrl_EnqueuesNothing()
    {
        var h = await CreateEventHarness();
        var pid = Id.New<Pipeline>();
        var binding = Pick(h.Svc.Create(pid, "acme/widgets", "main", ".pipelines", "GITHUB_TOKEN"));
        h.Queue.Reader.TryRead(out _);
        h.HookState.Set(binding.Id, new BindingHookState(pid, "acme", "widgets", 5L, "GITHUB_TOKEN", UrlOf(binding.Id)));

        Pick(h.Svc.SetLastPushSha(binding.Id, "abc"));

        await Assert.That(h.Queue.Reader.TryRead(out _)).IsFalse();
    }

    [Test]
    public async Task BindingDeleted_WithLiveHook_EnqueuesDelete()
    {
        var h = await CreateEventHarness();
        var pid = Id.New<Pipeline>();
        var binding = Pick(h.Svc.Create(pid, "acme/widgets", "main", ".pipelines", "GITHUB_TOKEN"));
        h.Queue.Reader.TryRead(out _); // drain create
        h.HookState.Set(binding.Id, new BindingHookState(pid, "acme", "widgets", 5L, "GITHUB_TOKEN"));

        await Assert.That(h.Svc.Delete(binding.Id).Succeeded).IsTrue();

        await Assert.That(h.Queue.Reader.TryRead(out var work)).IsTrue();
        await Assert.That(work as DeleteBindingHookWork).IsNotNull();
    }
}
