using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Shared;
using Olve.Results;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests;

public class GitHubWebhookReceiverTests
{
    private const string Secret = "topsecret";
    private const string Branch = "main";

    private static (GitHubWebhookReceiver Receiver, EntityStore<Trigger> Store) CreateReceiver()
    {
        var store = new EntityStore<Trigger>([]);
        var triggers = new TriggerService(store, new IdProvider());
        var receiver = new GitHubWebhookReceiver(triggers, NullLogger<GitHubWebhookReceiver>.Instance);
        return (receiver, store);
    }

    private static Trigger AddGitHubTrigger(EntityStore<Trigger> store, string secret = Secret, string branch = Branch)
    {
        var trigger = new Trigger(
            Id.New<Trigger>(),
            Id.New<Pipeline>(),
            "on-push",
            new GitHubWebhookTarget("acme", "widgets", branch, "GITHUB_TOKEN"),
            secret,
            DateTimeOffset.UtcNow);
        store.Set(trigger);
        return trigger;
    }

    private static byte[] PushBody(string branch) => Encoding.UTF8.GetBytes($$"""{"ref":"refs/heads/{{branch}}"}""");

    private static string Sign(byte[] body, string secret = Secret) => GitHubWebhookSignature.Compute(secret, body);

    [Test]
    public async Task Evaluate_PushToConfiguredBranch_Fires()
    {
        var (receiver, store) = CreateReceiver();
        var trigger = AddGitHubTrigger(store);
        var body = PushBody(Branch);

        var action = receiver.Evaluate(trigger.Id, body, Sign(body), "push");

        await Assert.That(action).IsEqualTo(GitHubWebhookAction.Fire);
    }

    [Test]
    public async Task Evaluate_PushToOtherBranch_Ignores()
    {
        var (receiver, store) = CreateReceiver();
        var trigger = AddGitHubTrigger(store);
        var body = PushBody("feature/x");

        var action = receiver.Evaluate(trigger.Id, body, Sign(body), "push");

        await Assert.That(action).IsEqualTo(GitHubWebhookAction.Ignore);
    }

    [Test]
    public async Task Evaluate_PingEvent_Ignores()
    {
        var (receiver, store) = CreateReceiver();
        var trigger = AddGitHubTrigger(store);
        var body = Encoding.UTF8.GetBytes("""{"zen":"Keep it simple."}""");

        var action = receiver.Evaluate(trigger.Id, body, Sign(body), "ping");

        await Assert.That(action).IsEqualTo(GitHubWebhookAction.Ignore);
    }

    [Test]
    public async Task Evaluate_InvalidSignature_Rejected()
    {
        var (receiver, store) = CreateReceiver();
        var trigger = AddGitHubTrigger(store);
        var body = PushBody(Branch);

        var action = receiver.Evaluate(trigger.Id, body, Sign(body, "wrong-secret"), "push");

        await Assert.That(action).IsEqualTo(GitHubWebhookAction.InvalidSignature);
    }

    [Test]
    public async Task Evaluate_UnknownTrigger_NotFound()
    {
        var (receiver, _) = CreateReceiver();
        var body = PushBody(Branch);

        var action = receiver.Evaluate(Id.New<Trigger>(), body, Sign(body), "push");

        await Assert.That(action).IsEqualTo(GitHubWebhookAction.NotFound);
    }

    [Test]
    public async Task Evaluate_NonGitHubTrigger_NotFound()
    {
        var (receiver, store) = CreateReceiver();
        var trigger = new Trigger(
            Id.New<Trigger>(), Id.New<Pipeline>(), "poll", new ProductionTriggerTarget(), Secret, DateTimeOffset.UtcNow);
        store.Set(trigger);
        var body = PushBody(Branch);

        var action = receiver.Evaluate(trigger.Id, body, Sign(body), "push");

        await Assert.That(action).IsEqualTo(GitHubWebhookAction.NotFound);
    }

    [Test]
    public async Task ExecuteInternal_GitHubTrigger_FiresProduction()
    {
        // Wire a minimal TriggerExecutionService and confirm a GitHubWebhookTarget routes to a
        // production run (the switch arm added in Phase 1).
        var triggerStore = new EntityStore<Trigger>([]);
        var pipelineStore = new EntityStore<Pipeline>([]);
        var productionStepStore = new EntityStore<ProductionStep>([]);
        var productionConfig = new AttachmentStore<ProductionStep, StepConfiguration>(productionStepStore);
        var processingStepStore = new EntityStore<ProcessingStep>([]);
        var processingConfig = new AttachmentStore<ProcessingStep, StepConfiguration>(processingStepStore);
        var promotionStore = new AttachmentStore<ProcessingStep, ProcessingStepPromotion>(processingStepStore);
        var bundleStore = new EntityStore<ArtifactBundle>([]);
        var jobStore = new EntityStore<Job>([]);
        var jobGroupStore = new EntityStore<JobGroup>([]);

        var idProvider = new IdProvider();
        var timeProvider = TimeProvider.System;

        var pipelines = new PipelineService(pipelineStore, idProvider);
        var productionSteps = new ProductionStepService(productionStepStore, productionConfig, idProvider);
        var processingSteps = new ProcessingStepService(processingStepStore, processingConfig, idProvider);
        var bundles = new ArtifactBundleService(bundleStore);
        var jobGroups = new JobGroupService(jobGroupStore, idProvider, timeProvider);
        var jobs = new JobService(NullLogger<JobService>.Instance, jobStore, jobGroups, idProvider, timeProvider);
        var promotionGate = new PromotionGateService(promotionStore);
        var pauseState = new ReconcilePauseState();

        var execution = new TriggerExecutionService(
            triggerStore, pipelines, productionSteps, processingSteps, bundles,
            jobGroups, jobs, promotionGate, pauseState);

        var pipeline = Pick(pipelines.Create("demo"));
        var step = Pick(productionSteps.Create(pipeline.Id, "build"));
        productionSteps.SetConfiguration(step.Id, new StepConfiguration("alpine", "echo hi", []));

        var trigger = new Trigger(
            Id.New<Trigger>(), pipeline.Id, "on-push",
            new GitHubWebhookTarget("acme", "widgets", Branch, "GITHUB_TOKEN"), Secret, DateTimeOffset.UtcNow);
        triggerStore.Set(trigger);

        var result = execution.ExecuteInternal(trigger.Id);

        await Assert.That(result.Succeeded).IsTrue();
        var productionJobs = jobs.ListJobs().OfType<Job.ProductionJob>().ToArray();
        await Assert.That(productionJobs).Count().IsEqualTo(1);
        await Assert.That(productionJobs[0].PipelineId).IsEqualTo(pipeline.Id);
    }

    private static T Pick<T>(Result<T> result)
    {
        result.TryPickProblems(out _, out var value);
        return value!;
    }
}
