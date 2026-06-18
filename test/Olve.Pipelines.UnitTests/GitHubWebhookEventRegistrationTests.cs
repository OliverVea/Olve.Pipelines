using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.GitHub;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Shared;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests;

public class GitHubWebhookEventRegistrationTests
{
    private record Harness(
        EntityStore<Trigger> Store,
        GitHubHookStateStore HookState,
        GitHubHookWorkQueue Queue);

    private static Harness CreateHarness(string? baseUrl = "https://hooks.example.com")
    {
        var store = new EntityStore<Trigger>([]);
        var events = new TriggerEvents();
        var hookState = new GitHubHookStateStore();
        var queue = new GitHubHookWorkQueue();

        // Mirror the production forwarding (store CRUD events -> domain hub).
        store.OnAdded.Subscribe(events.OnAdded.Invoke);
        store.OnDeleted.Subscribe(events.OnDeleted.Invoke);

        var registration = new GitHubWebhookEventRegistration(
            events, store, hookState, queue, new WebhookOptions(baseUrl),
            NullLogger<GitHubWebhookEventRegistration>.Instance);
        registration.Run();

        return new Harness(store, hookState, queue);
    }

    private static Trigger GitHubTrigger(Id<Pipeline> pipelineId)
        => new(Id.New<Trigger>(), pipelineId, "on-push",
            new GitHubWebhookTarget("acme", "widgets", "main", "GITHUB_TOKEN"), "secret", DateTimeOffset.UtcNow);

    [Test]
    public async Task TriggerAdded_GitHubTarget_EnqueuesCreateWork()
    {
        var h = CreateHarness();
        var pipelineId = Id.New<Pipeline>();
        var trigger = GitHubTrigger(pipelineId);

        h.Store.Set(trigger);

        await Assert.That(h.Queue.Reader.TryRead(out var work)).IsTrue();
        var create = work as CreateHookWork;
        await Assert.That(create).IsNotNull();
        await Assert.That(create!.TriggerId).IsEqualTo(trigger.Id);
        await Assert.That(create.Owner).IsEqualTo("acme");
        await Assert.That(create.HookSecret).IsEqualTo("secret");
    }

    [Test]
    public async Task TriggerAdded_NonGitHubTarget_EnqueuesNothing()
    {
        var h = CreateHarness();
        var trigger = new Trigger(Id.New<Trigger>(), Id.New<Pipeline>(), "prod",
            new ProductionTriggerTarget(), "secret", DateTimeOffset.UtcNow);

        h.Store.Set(trigger);

        await Assert.That(h.Queue.Reader.TryRead(out _)).IsFalse();
    }

    [Test]
    public async Task TriggerAdded_NoPublicBaseUrl_EnqueuesNothing()
    {
        var h = CreateHarness(baseUrl: null);

        h.Store.Set(GitHubTrigger(Id.New<Pipeline>()));

        await Assert.That(h.Queue.Reader.TryRead(out _)).IsFalse();
    }

    [Test]
    public async Task TriggerAdded_AlreadyHasHookState_EnqueuesNothing()
    {
        var h = CreateHarness();
        var trigger = GitHubTrigger(Id.New<Pipeline>());
        h.HookState.Set(trigger.Id, new GitHubHookState(trigger.PipelineId, "acme", "widgets", 1L, "GITHUB_TOKEN"));

        h.Store.Set(trigger);

        await Assert.That(h.Queue.Reader.TryRead(out _)).IsFalse();
    }

    [Test]
    public async Task TriggerDeleted_WithHookState_EnqueuesDeleteWork()
    {
        var h = CreateHarness();
        var pipelineId = Id.New<Pipeline>();
        var trigger = GitHubTrigger(pipelineId);
        h.Store.Set(trigger);
        h.Queue.Reader.TryRead(out _); // drain the create-work from the add above
        h.HookState.Set(trigger.Id, new GitHubHookState(pipelineId, "acme", "widgets", 999L, "GITHUB_TOKEN"));

        h.Store.Delete(trigger.Id);

        await Assert.That(h.Queue.Reader.TryRead(out var work)).IsTrue();
        var delete = work as DeleteHookWork;
        await Assert.That(delete).IsNotNull();
        await Assert.That(delete!.TriggerId).IsEqualTo(trigger.Id);
        await Assert.That(delete.HookId).IsEqualTo(999L);
    }

    [Test]
    public async Task TriggerDeleted_WithoutHookState_EnqueuesNothing()
    {
        var h = CreateHarness();
        var trigger = GitHubTrigger(Id.New<Pipeline>());
        h.Store.Set(trigger);
        h.Queue.Reader.TryRead(out _); // drain the create-work

        h.Store.Delete(trigger.Id);

        await Assert.That(h.Queue.Reader.TryRead(out _)).IsFalse();
    }
}
