using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Building;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Processing;
using Olve.Pipelines.Shared;
using Olve.Pipelines.Sourcing;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;
using static Olve.Pipelines.Jobs.Job;
using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.UnitTests;

public class JobServiceTests
{
    private static JobService CreateService()
    {
        var store = new EntityStore<Job>([]);
        return new JobService(
            NullLogger<JobService>.Instance,
            store,
            new IdProvider(),
            TimeProvider.System);
    }

    [Test]
    public async Task CreateSourcingJob_ReturnsScheduledJob()
    {
        var service = CreateService();
        var pipelineId = Id.New<Pipeline>();

        var result = service.CreateSourcingJob(pipelineId);

        await Assert.That(result).Succeeded();
        result.TryPickProblems(out _, out var job);
        await Assert.That(job).IsTypeOf<SourcingJob>();
        await Assert.That(job!.Status).IsTypeOf<Scheduled>();
        await Assert.That(job.PipelineId).IsEqualTo(pipelineId);
    }

    [Test]
    public async Task CreateBuildJob_ReturnsScheduledJob()
    {
        var service = CreateService();
        var pipelineId = Id.New<Pipeline>();
        var sourceBundleId = Id.New<SourceBundle>();

        var result = service.CreateBuildJob(pipelineId, sourceBundleId);

        await Assert.That(result).Succeeded();
        result.TryPickProblems(out _, out var job);
        await Assert.That(job).IsTypeOf<BuildJob>();
        await Assert.That(((BuildJob)job!).SourceBundleId).IsEqualTo(sourceBundleId);
        await Assert.That(job.Status).IsTypeOf<Scheduled>();
        await Assert.That(job.PipelineId).IsEqualTo(pipelineId);
    }

    [Test]
    public async Task CreateProcessingJob_ReturnsScheduledJob()
    {
        var service = CreateService();
        var pipelineId = Id.New<Pipeline>();
        var artifactBundleId = Id.New<ArtifactBundle>();
        var processingStepId = Id.New<ProcessingStep>();

        var result = service.CreateProcessingJob(pipelineId, artifactBundleId, processingStepId);

        await Assert.That(result).Succeeded();
        result.TryPickProblems(out _, out var job);
        await Assert.That(job).IsTypeOf<ProcessingJob>();
        var pj = (ProcessingJob)job!;
        await Assert.That(pj.Status).IsTypeOf<Scheduled>();
        await Assert.That(pj.PipelineId).IsEqualTo(pipelineId);
        await Assert.That(pj.ArtifactBundleId).IsEqualTo(artifactBundleId);
        await Assert.That(pj.ProcessingStepId).IsEqualTo(processingStepId);
    }
}
