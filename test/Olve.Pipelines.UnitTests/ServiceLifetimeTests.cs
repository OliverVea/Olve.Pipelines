using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Pipelines.Sync;

namespace Olve.Pipelines.UnitTests;

/// <summary>
/// Services that own an <c>EntityStoreIndex</c> subscribe to their store on construction and never
/// unsubscribe, so each resolution of a transient owner leaked an index (#26: the controller OOMed
/// as job events resolved them). Resolving one twice must yield the same instance.
/// </summary>
public class ServiceLifetimeTests
{
    [Test]
    [Arguments(typeof(AttemptService))]
    [Arguments(typeof(RunService))]
    [Arguments(typeof(PipelineVersionService))]
    [Arguments(typeof(PipelineActivationService))]
    [Arguments(typeof(PipelineNumberingService))]
    [Arguments(typeof(PipelineConfigBindingService))]
    [Arguments(typeof(PipelineConfigBindingCleanupService))]
    [Arguments(typeof(Olve.Pipelines.Pipelines.Scheduling.RunSchedulingLock))]
    [Arguments(typeof(Olve.Pipelines.Pipelines.Scheduling.RunPlanCache))]
    public async Task IndexOwningService_ResolvedTwice_IsSameInstance(Type serviceType)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddPipelineServices();
        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService(serviceType);
        var second = provider.GetRequiredService(serviceType);

        await Assert.That(ReferenceEquals(first, second)).IsTrue();
    }
}
