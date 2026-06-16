using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Shared;
using Olve.Results;
using Olve.Utilities.Ids;
using static Olve.Pipelines.Jobs.Job;
using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.UnitTests;

public class DownstreamTriggerServiceTests
{
    private record Services(
        EntityStore<Job> JobStore,
        EntityStore<JobGroup> JobGroupStore,
        EntityStore<ProcessingStep> ProcessingStepStore,
        AttachmentStore<ProcessingStep, StepConfiguration> ProcessingStepConfig,
        PromotionGateService PromotionGate,
        JobService JobService,
        JobGroupService JobGroupService,
        ProcessingStepService ProcessingStepService,
        JobGroupCompletionService CompletionService,
        DownstreamTriggerService DownstreamTriggerService,
        JobEvents Events);

    private static Services CreateServices()
    {
        var jobStore = new EntityStore<Job>([]);
        var jobGroupStore = new EntityStore<JobGroup>([]);
        var processingStepStore = new EntityStore<ProcessingStep>([]);
        var processingStepConfig = new AttachmentStore<ProcessingStep, StepConfiguration>(processingStepStore);
        var bundleStore = new EntityStore<ArtifactBundle>([]);
        var idProvider = new IdProvider();
        var timeProvider = new TimeProviderMake().Instance();
        var events = new JobEvents();

        jobStore.OnAdded.Subscribe(events.OnAdded.Invoke);
        jobStore.OnUpdated.Subscribe(events.OnUpdated.Invoke);

        var jobGroupService = new JobGroupService(jobGroupStore, idProvider, timeProvider);
        var jobService = new JobService(NullLogger<JobService>.Instance, jobStore, jobGroupService, idProvider, timeProvider);
        var bundleService = new ArtifactBundleService(bundleStore);
        var processingStepService = new ProcessingStepService(processingStepStore, processingStepConfig, idProvider);
        var promotionStore = new AttachmentStore<ProcessingStep, ProcessingStepPromotion>(processingStepStore);
        var promotionGate = new PromotionGateService(promotionStore);

        var completionService = new JobGroupCompletionService(
            jobService, jobGroupService, bundleService, events,
            NullLogger<JobGroupCompletionService>.Instance);

        var downstreamTriggerService = new DownstreamTriggerService(
            jobGroupService, processingStepService, promotionGate, jobService,
            NullLogger<DownstreamTriggerService>.Instance);

        events.OnUpdated.Subscribe(completionService.HandleJobUpdated);
        events.OnGroupCompleted.Subscribe(downstreamTriggerService.HandleGroupCompleted);

        return new Services(
            jobStore, jobGroupStore, processingStepStore, processingStepConfig, promotionGate,
            jobService, jobGroupService, processingStepService,
            completionService, downstreamTriggerService, events);
    }

    private static T Pick<T>(Result<T> result)
    {
        result.TryPickProblems(out _, out var value);
        return value!;
    }

    private static void CompleteJob(Services svc, Id<Job> jobId)
    {
        svc.JobService.TryGetJob<Job>(jobId, out var job);
        var now = DateTimeOffset.UtcNow;
        switch (job)
        {
            case ProductionJob:
                svc.JobService.UpdateJob<ProductionJob>(jobId, j => j with
                {
                    Status = new Done(now, now),
                });
                break;
            case ProcessingJob:
                svc.JobService.UpdateJob<ProcessingJob>(jobId, j => j with
                {
                    Status = new Done(now, now),
                });
                break;
        }
    }

    private static void FailJob(Services svc, Id<Job> jobId)
    {
        svc.JobService.TryGetJob<Job>(jobId, out var job);
        var now = DateTimeOffset.UtcNow;
        switch (job)
        {
            case ProductionJob:
                svc.JobService.UpdateJob<ProductionJob>(jobId, j => j with
                {
                    Status = new Failed(now, now),
                });
                break;
            case ProcessingJob:
                svc.JobService.UpdateJob<ProcessingJob>(jobId, j => j with
                {
                    Status = new Failed(now, now),
                });
                break;
        }
    }

    [Test]
    public async Task ProductionGroupCompleted_TriggersFirstProcessingStep()
    {
        var svc = CreateServices();
        var pipelineId = Id.New<Pipeline>();

        var step1 = Pick(svc.ProcessingStepService.Create(pipelineId, "deploy-staging", 1));
        var step2 = Pick(svc.ProcessingStepService.Create(pipelineId, "deploy-prod", 2));
        svc.ProcessingStepService.SetConfiguration(step1.Id, new StepConfiguration("image", "echo deploy", []));
        svc.ProcessingStepService.SetConfiguration(step2.Id, new StepConfiguration("image", "echo deploy", []));

        var bundleId = Id.New<ArtifactBundle>();
        var prodGroup = svc.JobGroupService.CreateProductionGroup(pipelineId, bundleId);
        var prodJob = Pick(svc.JobService.CreateProductionJob(pipelineId, prodGroup.Id, Id.New<ProductionStep>()));

        CompleteJob(svc, prodJob.Id);

        var processingJobs = svc.JobService.ListJobs().OfType<ProcessingJob>().ToArray();

        await Assert.That(processingJobs).Count().IsEqualTo(1);
        await Assert.That(processingJobs[0].ProcessingStepId).IsEqualTo(step1.Id);
        await Assert.That(processingJobs[0].ArtifactBundleId).IsEqualTo(bundleId);
        await Assert.That(processingJobs[0].Status).IsTypeOf<Scheduled>();
    }

    [Test]
    public async Task ProcessingGroupCompleted_TriggersNextProcessingStep()
    {
        var svc = CreateServices();
        var pipelineId = Id.New<Pipeline>();
        var bundleId = Id.New<ArtifactBundle>();

        var step1 = Pick(svc.ProcessingStepService.Create(pipelineId, "deploy-staging", 1));
        var step2 = Pick(svc.ProcessingStepService.Create(pipelineId, "deploy-prod", 2));
        svc.ProcessingStepService.SetConfiguration(step1.Id, new StepConfiguration("image", "echo 1", []));
        svc.ProcessingStepService.SetConfiguration(step2.Id, new StepConfiguration("image", "echo 2", []));

        var group1 = svc.JobGroupService.CreateProcessingGroup(pipelineId, bundleId, step1.Id);
        var job1 = Pick(svc.JobService.CreateProcessingJob(pipelineId, group1.Id, bundleId, step1.Id));

        CompleteJob(svc, job1.Id);

        var step2Jobs = svc.JobService.ListJobs().OfType<ProcessingJob>().Where(j => j.ProcessingStepId == step2.Id).ToArray();

        await Assert.That(step2Jobs).Count().IsEqualTo(1);
        await Assert.That(step2Jobs[0].ArtifactBundleId).IsEqualTo(bundleId);
        await Assert.That(step2Jobs[0].Status).IsTypeOf<Scheduled>();
    }

    [Test]
    public async Task LastProcessingStepCompleted_NoMoreJobsCreated()
    {
        var svc = CreateServices();
        var pipelineId = Id.New<Pipeline>();
        var bundleId = Id.New<ArtifactBundle>();

        var step1 = Pick(svc.ProcessingStepService.Create(pipelineId, "deploy", 1));
        svc.ProcessingStepService.SetConfiguration(step1.Id, new StepConfiguration("image", "echo 1", []));

        var group = svc.JobGroupService.CreateProcessingGroup(pipelineId, bundleId, step1.Id);
        var job = Pick(svc.JobService.CreateProcessingJob(pipelineId, group.Id, bundleId, step1.Id));

        CompleteJob(svc, job.Id);

        var processingJobs = svc.JobService.ListJobs().OfType<ProcessingJob>().ToArray();
        await Assert.That(processingJobs).Count().IsEqualTo(1);
    }

    [Test]
    public async Task ProductionGroupFailed_NoProcessingTriggered()
    {
        var svc = CreateServices();
        var pipelineId = Id.New<Pipeline>();

        Pick(svc.ProcessingStepService.Create(pipelineId, "deploy", 1));
        svc.ProcessingStepService.SetConfiguration(
            Pick(svc.ProcessingStepService.GetByPipelineId(pipelineId))[0].Id,
            new StepConfiguration("image", "echo 1", []));

        var bundleId = Id.New<ArtifactBundle>();
        var prodGroup = svc.JobGroupService.CreateProductionGroup(pipelineId, bundleId);
        var prodJob = Pick(svc.JobService.CreateProductionJob(pipelineId, prodGroup.Id, Id.New<ProductionStep>()));

        FailJob(svc, prodJob.Id);

        var processingJobs = svc.JobService.ListJobs().OfType<ProcessingJob>().ToArray();
        await Assert.That(processingJobs).Count().IsEqualTo(0);
    }

    [Test]
    public async Task ProcessingGroupFailed_NoNextStepTriggered()
    {
        var svc = CreateServices();
        var pipelineId = Id.New<Pipeline>();
        var bundleId = Id.New<ArtifactBundle>();

        var step1 = Pick(svc.ProcessingStepService.Create(pipelineId, "deploy-staging", 1));
        Pick(svc.ProcessingStepService.Create(pipelineId, "deploy-prod", 2));
        svc.ProcessingStepService.SetConfiguration(step1.Id, new StepConfiguration("image", "echo 1", []));
        svc.ProcessingStepService.SetConfiguration(
            Pick(svc.ProcessingStepService.GetByPipelineId(pipelineId))[1].Id,
            new StepConfiguration("image", "echo 2", []));

        var group = svc.JobGroupService.CreateProcessingGroup(pipelineId, bundleId, step1.Id);
        var job = Pick(svc.JobService.CreateProcessingJob(pipelineId, group.Id, bundleId, step1.Id));

        FailJob(svc, job.Id);

        var processingJobs = svc.JobService.ListJobs().OfType<ProcessingJob>().ToArray();
        await Assert.That(processingJobs).Count().IsEqualTo(1);
        await Assert.That(processingJobs[0].ProcessingStepId).IsEqualTo(step1.Id);
    }

    [Test]
    public async Task NoProcessingSteps_ProductionCompletes_NoJobsCreated()
    {
        var svc = CreateServices();
        var pipelineId = Id.New<Pipeline>();
        var bundleId = Id.New<ArtifactBundle>();

        var prodGroup = svc.JobGroupService.CreateProductionGroup(pipelineId, bundleId);
        var prodJob = Pick(svc.JobService.CreateProductionJob(pipelineId, prodGroup.Id, Id.New<ProductionStep>()));

        CompleteJob(svc, prodJob.Id);

        var processingJobs = svc.JobService.ListJobs().OfType<ProcessingJob>().ToArray();
        await Assert.That(processingJobs).Count().IsEqualTo(0);
    }

    [Test]
    public async Task PromotionBlocked_FirstStep_NotTriggered()
    {
        var svc = CreateServices();
        var pipelineId = Id.New<Pipeline>();

        var step1 = Pick(svc.ProcessingStepService.Create(pipelineId, "deploy", 1));
        svc.ProcessingStepService.SetConfiguration(step1.Id, new StepConfiguration("image", "echo deploy", []));
        svc.PromotionGate.Block(step1.Id);

        var bundleId = Id.New<ArtifactBundle>();
        var prodGroup = svc.JobGroupService.CreateProductionGroup(pipelineId, bundleId);
        var prodJob = Pick(svc.JobService.CreateProductionJob(pipelineId, prodGroup.Id, Id.New<ProductionStep>()));

        CompleteJob(svc, prodJob.Id);

        var processingJobs = svc.JobService.ListJobs().OfType<ProcessingJob>().ToArray();
        await Assert.That(processingJobs).Count().IsEqualTo(0);
    }

    [Test]
    public async Task PromotionBlocked_NextStep_HaltsChainWithoutSkipping()
    {
        var svc = CreateServices();
        var pipelineId = Id.New<Pipeline>();
        var bundleId = Id.New<ArtifactBundle>();

        var step1 = Pick(svc.ProcessingStepService.Create(pipelineId, "step-1", 1));
        var step2 = Pick(svc.ProcessingStepService.Create(pipelineId, "step-2", 2));
        var step3 = Pick(svc.ProcessingStepService.Create(pipelineId, "step-3", 3));
        svc.ProcessingStepService.SetConfiguration(step1.Id, new StepConfiguration("img", "s1", []));
        svc.ProcessingStepService.SetConfiguration(step2.Id, new StepConfiguration("img", "s2", []));
        svc.ProcessingStepService.SetConfiguration(step3.Id, new StepConfiguration("img", "s3", []));

        // Block the middle step: completing step1 must NOT trigger step2, and must NOT skip to step3.
        svc.PromotionGate.Block(step2.Id);

        var group1 = svc.JobGroupService.CreateProcessingGroup(pipelineId, bundleId, step1.Id);
        var job1 = Pick(svc.JobService.CreateProcessingJob(pipelineId, group1.Id, bundleId, step1.Id));

        CompleteJob(svc, job1.Id);

        var laterJobs = svc.JobService.ListJobs().OfType<ProcessingJob>()
            .Where(j => j.ProcessingStepId == step2.Id || j.ProcessingStepId == step3.Id).ToArray();
        await Assert.That(laterJobs).Count().IsEqualTo(0);
    }

    [Test]
    public async Task PromotionUnblocked_ResumesCascade()
    {
        var svc = CreateServices();
        var pipelineId = Id.New<Pipeline>();
        var bundleId = Id.New<ArtifactBundle>();

        var step1 = Pick(svc.ProcessingStepService.Create(pipelineId, "step-1", 1));
        var step2 = Pick(svc.ProcessingStepService.Create(pipelineId, "step-2", 2));
        svc.ProcessingStepService.SetConfiguration(step1.Id, new StepConfiguration("img", "s1", []));
        svc.ProcessingStepService.SetConfiguration(step2.Id, new StepConfiguration("img", "s2", []));

        svc.PromotionGate.Block(step2.Id);
        svc.PromotionGate.Unblock(step2.Id);

        var group1 = svc.JobGroupService.CreateProcessingGroup(pipelineId, bundleId, step1.Id);
        var job1 = Pick(svc.JobService.CreateProcessingJob(pipelineId, group1.Id, bundleId, step1.Id));

        CompleteJob(svc, job1.Id);

        var step2Jobs = svc.JobService.ListJobs().OfType<ProcessingJob>().Where(j => j.ProcessingStepId == step2.Id).ToArray();
        await Assert.That(step2Jobs).Count().IsEqualTo(1);
    }

    [Test]
    public async Task FullCascade_ThreeSteps_AllTriggeredSequentially()
    {
        var svc = CreateServices();
        var pipelineId = Id.New<Pipeline>();
        var bundleId = Id.New<ArtifactBundle>();

        var step1 = Pick(svc.ProcessingStepService.Create(pipelineId, "step-1", 1));
        var step2 = Pick(svc.ProcessingStepService.Create(pipelineId, "step-2", 2));
        var step3 = Pick(svc.ProcessingStepService.Create(pipelineId, "step-3", 3));
        svc.ProcessingStepService.SetConfiguration(step1.Id, new StepConfiguration("img", "s1", []));
        svc.ProcessingStepService.SetConfiguration(step2.Id, new StepConfiguration("img", "s2", []));
        svc.ProcessingStepService.SetConfiguration(step3.Id, new StepConfiguration("img", "s3", []));

        // Production completes -> triggers step1
        var prodGroup = svc.JobGroupService.CreateProductionGroup(pipelineId, bundleId);
        var prodJob = Pick(svc.JobService.CreateProductionJob(pipelineId, prodGroup.Id, Id.New<ProductionStep>()));
        CompleteJob(svc, prodJob.Id);

        var step1Jobs = svc.JobService.ListJobs().OfType<ProcessingJob>().Where(j => j.ProcessingStepId == step1.Id).ToArray();
        await Assert.That(step1Jobs).Count().IsEqualTo(1);

        // Step1 completes -> triggers step2
        CompleteJob(svc, step1Jobs[0].Id);

        var step2Jobs = svc.JobService.ListJobs().OfType<ProcessingJob>().Where(j => j.ProcessingStepId == step2.Id).ToArray();
        await Assert.That(step2Jobs).Count().IsEqualTo(1);

        // Step2 completes -> triggers step3
        CompleteJob(svc, step2Jobs[0].Id);

        var step3Jobs = svc.JobService.ListJobs().OfType<ProcessingJob>().Where(j => j.ProcessingStepId == step3.Id).ToArray();
        await Assert.That(step3Jobs).Count().IsEqualTo(1);

        // Step3 completes -> no more
        CompleteJob(svc, step3Jobs[0].Id);

        var allProcessingJobs = svc.JobService.ListJobs().OfType<ProcessingJob>().ToArray();
        await Assert.That(allProcessingJobs).Count().IsEqualTo(3);
    }
}
