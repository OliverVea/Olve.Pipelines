using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Sources;

namespace Olve.Pipelines.UnitTests.Sources;

public class PollSourceWiringTests
{
    [Test]
    public async Task Container_RegistersBothProviders_AndTheBindingProviderIsTheDeployPollSingleton()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddPipelineServices();
        await using var provider = services.BuildServiceProvider();

        var providers = provider.GetServices<IPollSourceProvider>().ToList();

        await Assert.That(providers.Count).IsEqualTo(2);
        await Assert.That(providers.OfType<PollTriggerService>().Count()).IsEqualTo(1);
        await Assert.That(ReferenceEquals(providers.OfType<DeployPollService>().Single(), provider.GetRequiredService<DeployPollService>())).IsTrue();
    }
}
