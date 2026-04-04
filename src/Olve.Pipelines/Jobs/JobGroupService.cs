using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Jobs;

public class JobGroupService(EntityStore<JobGroup> store, IdProvider idProvider, TimeProvider timeProvider)
{
    public ProductionJobGroup CreateProductionGroup(Id<Pipeline> pipelineId, Id<ArtifactBundle> artifactBundleId)
    {
        var group = new ProductionJobGroup(idProvider.Create<JobGroup>(), pipelineId, timeProvider.GetUtcNow(), artifactBundleId);
        store.Set(group);
        return group;
    }

    public ProcessingJobGroup CreateProcessingGroup(Id<Pipeline> pipelineId, Id<ArtifactBundle> artifactBundleId, Id<ProcessingStep> processingStepId)
    {
        var group = new ProcessingJobGroup(idProvider.Create<JobGroup>(), pipelineId, timeProvider.GetUtcNow(), artifactBundleId, processingStepId);
        store.Set(group);
        return group;
    }

    public bool TryGet(Id<JobGroup> id, out JobGroup? group) => store.TryGet(id, out group);

    public IReadOnlyList<JobGroup> List() => store.List();
}
