using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Configs;
using Olve.Pipelines.Pipelines.Failures;
using Olve.Pipelines.Pipelines.Retention;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Shared;
using Olve.Pipelines.Shared.Persistence;
using Olve.Pipelines.Sources;
using Olve.Pipelines.Streaming;
using Olve.Pipelines.UnitTests.Manifests;
using Olve.Results.TUnit;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.UnitTests.Scheduling;

/// <summary>
/// The scheduler through the real DI registrations (<see cref="ServiceConfiguration.AddPipelineServices"/>).
/// Store events swallow subscriber exceptions, so an unresolvable handler graph would silently disable
/// scheduling while the hand-wired scenario tests stay green.
/// </summary>
public class ProductionWiringTests
{
    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddPipelineServices();
        return services.BuildServiceProvider();
    }

    [Test]
    [Arguments(typeof(RunSchedulingHandler))]
    [Arguments(typeof(StageScheduler))]
    [Arguments(typeof(RunStartService))]
    [Arguments(typeof(RunContinuationService))]
    [Arguments(typeof(SwitchService))]
    [Arguments(typeof(StepBlockService))]
    [Arguments(typeof(AttemptLaunchService))]
    public async Task SchedulerService_Resolves(Type serviceType)
    {
        await using var provider = Build();

        await Assert.That(provider.GetRequiredService(serviceType)).IsNotNull();
    }

    // Catches: a lost registration, which fails silently: persistence, retention, latest-wins, failure handlers
    // or poll triggers just stop, with no exception. Beta-test sees some of these, not persistence across a restart.
    [Test]
    public async Task EveryBackgroundServiceAndEventRegistration_IsRegistered()
    {
        var host = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        host.Services.AddSingleton(new StorageOptions("bucket", Mode: StorageMode.Ephemeral));
        host.Services.AddPipelineServices();
        await using var provider = host.Services.BuildServiceProvider();

        await Assert.That(provider.GetServices<IHostedService>().Select(s => s.GetType()).Distinct()).IsEquivalentTo(new[]
        {
            typeof(BindingHookRegistrationService), typeof(BindingHookPersistenceService), typeof(DeployPollService), typeof(PushTriggerService),
            typeof(PollScheduler), typeof(WebhookTriggerService), typeof(JobRunner), typeof(StartupRunner),
            typeof(ConfigurationPersistenceService), typeof(RunPersistenceService), typeof(RetentionService),
        });
        await Assert.That(provider.GetServices<IRunOnStartup>().Select(s => s.GetType())).IsEquivalentTo(new[]
        {
            typeof(PipelineSecretEventRegistration), typeof(PipelineConfigBindingEventRegistration), typeof(BindingWebhookEventRegistration),
            typeof(ConfigEventRegistration), typeof(FailureHandlerEventRegistration), typeof(JobEventRegistration),
            typeof(RunSchedulingEventRegistration), typeof(RunSchedulingStartup), typeof(ChangeEventRegistration),
            typeof(PipelineEventRegistration), typeof(PipelineVersionEventRegistration), typeof(RunEventRegistration),
        });
    }

    // Catches: the rules these registrations' order carries. The registry drains watchers before persistence
    // flushes, and the sweep's writes must reach the other event registrations.
    [Test]
    public async Task OrderDependentRegistrations_StayInOrder()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddPipelineServices();
        var startups = services.Where(s => s.ServiceType == typeof(IRunOnStartup)).Select(s => s.ImplementationType).ToList();
        var hostedRegistration = services.ToList().FindIndex(s => s.ServiceType == typeof(IHostedLifecycleService));
        var persistence = services.ToList().FindIndex(s => s.ImplementationType == typeof(RunPersistenceService));

        foreach (var earlier in (Type[])[typeof(RunEventRegistration), typeof(ConfigEventRegistration), typeof(FailureHandlerEventRegistration), typeof(JobEventRegistration)])
            await Assert.That(startups.IndexOf(typeof(RunSchedulingEventRegistration))).IsGreaterThan(startups.IndexOf(earlier));
        await Assert.That(hostedRegistration).IsLessThan(persistence);
    }

    [Test]
    public async Task StartupRegistrations_RunAndSchedulePipelineWork()
    {
        await using var provider = Build();
        var startups = provider.GetServices<IRunOnStartup>().ToList();
        await Assert.That(startups.Select(s => s.GetType())).Contains(typeof(RunSchedulingEventRegistration));
        await Assert.That(startups.Select(s => s.GetType())).Contains(typeof(RunSchedulingStartup));
        foreach (var startup in startups)
        {
            if (startup.Run().TryPickProblems(out var problems))
                throw new InvalidOperationException(string.Join("; ", problems.Select(p => p.ToString())));
        }

        var pipeline = TestRunDomain.Pick(provider.GetRequiredService<PipelineService>().Create("p"));
        var version = TestRunDomain.Pick(provider.GetRequiredService<PipelineVersionService>().Add(
            pipeline.Id, ManifestFixture.Text, "sha256:x", null, new Dictionary<string, int>(), new Dictionary<string, int>()));
        TestRunDomain.Pick(provider.GetRequiredService<SwitchService>().Switch(pipeline.Id, version.Id));

        var outcome = TestRunDomain.Pick(provider.GetRequiredService<RunStartService>()
            .Start(pipeline.Id, SchedulerScenario.Input(), new RunTrigger(RunTriggerKind.Manual)));

        var run = ((RunStartOutcome.Started)outcome).Run;
        var attempts = provider.GetRequiredService<AttemptService>();
        var checkout = attempts.ListForStep(run.Id, "checkout").Single();
        await Assert.That(checkout.Status).IsTypeOf<Scheduled>();

        // An attempt finishing reaches the scheduler through the registered event handlers.
        TestRunDomain.Pick(attempts.MarkInProgress(checkout.Id));
        if (attempts.MarkSucceeded(checkout.Id, SchedulerScenario.DefaultOutput(run.Number, "checkout", 1)).TryPickProblems(out _))
            throw new InvalidOperationException("could not finish checkout");
        await Assert.That(attempts.ListByRun(run.Id).Where(a => a.Status.IsActive()).Select(a => a.StepName).Order().ToList())
            .IsEquivalentTo(["build", "test"]);
    }

    [Test]
    public async Task DomainChanges_ReachTheEventStream()
    {
        await using var provider = Build();
        foreach (var startup in provider.GetServices<IRunOnStartup>())
            await Assert.That(startup.Run()).Succeeded();
        var pipeline = TestRunDomain.Pick(provider.GetRequiredService<PipelineService>().Create("p"));
        using var subscription = provider.GetRequiredService<ChangeBroadcaster>().Subscribe(pipeline.Id, lastEventId: null);

        var version = TestRunDomain.Pick(provider.GetRequiredService<PipelineVersionService>().Add(
            pipeline.Id, ManifestFixture.Text, "sha256:x", null, new Dictionary<string, int>(), new Dictionary<string, int>()));
        TestRunDomain.Pick(provider.GetRequiredService<SwitchService>().Switch(pipeline.Id, version.Id));
        TestRunDomain.Pick(provider.GetRequiredService<RunStartService>().Start(pipeline.Id, SchedulerScenario.Input(), new RunTrigger(RunTriggerKind.Manual)));
        await Assert.That(provider.GetRequiredService<StepBlockService>().Block(pipeline.Id, "deploy-beta")).Succeeded();

        var types = new List<string>();
        while (subscription.Reader.TryRead(out var item))
            types.Add(item.Data.EventType);
        await Assert.That(types).Contains("version");
        await Assert.That(types).Contains("run");
        await Assert.That(types).Contains("attempt");
        await Assert.That(types).Contains("blocked");
    }
}
