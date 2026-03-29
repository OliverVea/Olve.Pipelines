namespace Olve.Pipelines.Shared.Persistence;

public class BundlePersistenceService(
    EntityStore<Sourcing.SourceBundle> sourceStore,
    EntityStore<Building.ArtifactBundle> artifactStore,
    ILogger<BundlePersistenceService> logger,
    IBundleStore? bundleStore = null) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        if (bundleStore is null)
        {
            logger.LogWarning("Bundle store not configured, skipping bundle load");
            return;
        }

        try
        {
            var sourceBundles = await bundleStore.ListSourceBundlesAsync(cancellationToken);
            foreach (var bundle in sourceBundles)
                sourceStore.Set(bundle);

            var artifactBundles = await bundleStore.ListArtifactBundlesAsync(cancellationToken);
            foreach (var bundle in artifactBundles)
                artifactStore.Set(bundle);

            logger.LogInformation(
                "Loaded {SourceCount} source bundles and {ArtifactCount} artifact bundles from store",
                sourceBundles.Count,
                artifactBundles.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load bundles from store, starting fresh");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
