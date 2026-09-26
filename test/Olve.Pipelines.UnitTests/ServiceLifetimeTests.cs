using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Triggers;

namespace Olve.Pipelines.UnitTests;

/// <summary>
/// Services that own an <c>EntityStoreIndex</c> subscribe to their store on construction and never
/// unsubscribe, so each resolution of a transient owner leaked an index (#26: the controller OOMed
/// as job events resolved them). Resolving one twice must yield the same instance.
/// </summary>
public class ServiceLifetimeTests
{
    [Test]
    [Arguments(typeof(JobService))]
    [Arguments(typeof(ArtifactBundleService))]
    [Arguments(typeof(ProductionStepService))]
    [Arguments(typeof(ProductionStepCleanupService))]
    [Arguments(typeof(ProcessingStepService))]
    [Arguments(typeof(ProcessingStepCleanupService))]
    [Arguments(typeof(TriggerService))]
    [Arguments(typeof(TriggerCleanupService))]
    [Arguments(typeof(PipelineConfigBindingService))]
    [Arguments(typeof(PipelineConfigBindingCleanupService))]
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
