using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Shared;
using Olve.Pipelines.UnitTests.Manifests;
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
    [Arguments(typeof(StepGateService))]
    [Arguments(typeof(AttemptLaunchService))]
    public async Task SchedulerService_Resolves(Type serviceType)
    {
        await using var provider = Build();

        await Assert.That(provider.GetRequiredService(serviceType)).IsNotNull();
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
}
