using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;
using Olve.Pipelines.Sourcing;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests;

public class SourceBundleServiceTests
{
    [Test]
    public async Task Create_WithPendingStatus_SetsPending()
    {
        var store = new EntityStore<SourceBundle>([]);
        var service = new SourceBundleService(store);
        var pipelineId = Id.New<Pipeline>();

        var bundle = service.Create(pipelineId, SourceBundleStatus.Pending);

        await Assert.That(bundle.Status).IsEqualTo(SourceBundleStatus.Pending);
    }

    [Test]
    public async Task UpdateStatus_ChangesStatus()
    {
        var store = new EntityStore<SourceBundle>([]);
        var service = new SourceBundleService(store);
        var pipelineId = Id.New<Pipeline>();

        var bundle = service.Create(pipelineId, SourceBundleStatus.Pending);
        service.UpdateStatus(bundle.Id, SourceBundleStatus.Completed);

        service.TryGet(bundle.Id, out var updated);
        await Assert.That(updated!.Status).IsEqualTo(SourceBundleStatus.Completed);
    }

    [Test]
    public async Task Create_DefaultStatus_IsCompleted()
    {
        var store = new EntityStore<SourceBundle>([]);
        var service = new SourceBundleService(store);
        var pipelineId = Id.New<Pipeline>();

        var bundle = service.Create(pipelineId);

        await Assert.That(bundle.Status).IsEqualTo(SourceBundleStatus.Completed);
    }
}
