using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Shared;
using Olve.Results;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;
using static Olve.Pipelines.Jobs.Job;
using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.UnitTests;

/// <summary>
/// Promote and the unblock check end to end over the real stores, the real cascade and latest-wins
/// obsoletion — the scenarios of issue #41 and each edge case of the decision table. No
/// <see cref="ArtifactBundle"/> records are ever created here, as after a controller restart: the
/// promotion logic must work from the persisted job history alone.
/// </summary>
public class PromotionServiceTests
{
    private sealed class Harness
    {
        public readonly Id<Pipeline> PipelineId = Id.New<Pipeline>();
        public readonly EntityStore<Job> JobStore = new([]);
        public readonly EntityStore<JobGroup> GroupStore = new([]);
        public readonly JobService Jobs;
        public readonly JobGroupService Groups;
        public readonly ProcessingStepService Steps;
        public readonly PromotionGateService Gate;
        public readonly PromotionService Promotions;
        public readonly DownstreamTriggerService Cascade;
        private readonly MonotonicTimeProvider _time = new(new DateTimeOffset(2026, 10, 12, 5, 0, 0, TimeSpan.Zero), TimeSpan.FromSeconds(1));
        private readonly Dictionary<string, ProcessingStep> _byName = new();

        public Harness(params string[] stepNames)
        {
            var stepStore = new EntityStore<ProcessingStep>([]);
            var config = new AttachmentStore<ProcessingStep, StepConfiguration>(stepStore);
            var idProvider = new IdProvider();
            var events = new JobEvents();
            var locks = new PromotionLocks();

            Groups = new JobGroupService(GroupStore, idProvider, _time);
            Jobs = new JobService(NullLogger<JobService>.Instance, JobStore, Groups, idProvider, _time);
            Steps = new ProcessingStepService(stepStore, config, idProvider);
            Gate = new PromotionGateService(new AttachmentStore<ProcessingStep, ProcessingStepPromotion>(stepStore));
            Promotions = new PromotionService(Steps, Gate, Jobs, Groups, locks, NullLogger<PromotionService>.Instance);
            Cascade = new DownstreamTriggerService(Groups, Steps, Gate, locks, Jobs, NullLogger<DownstreamTriggerService>.Instance);

            var completion = new JobGroupCompletionService(
                Jobs, Groups, new ArtifactBundleService(new EntityStore<ArtifactBundle>([])), new JobGroupCompletionTracker(), events,
                NullLogger<JobGroupCompletionService>.Instance);
            var obsoletion = new JobObsoletionService(Jobs, NullLogger<JobObsoletionService>.Instance);

            JobStore.OnAdded.Subscribe(e => obsoletion.HandleJobAdded(e.Id));
            JobStore.OnUpdated.Subscribe(e => completion.HandleJobUpdated(e.Id));
            events.OnGroupCompleted.Subscribe(Cascade.HandleGroupCompleted);

            for (var i = 0; i < stepNames.Length; i++)
            {
                var parts = stepNames[i].Split(':');
                var name = parts[0];
                var step = Pick(Steps.Create(PipelineId, name, i + 1));
                if (parts.Length == 1 || parts[1] != "unconfigured")
                    Steps.SetConfiguration(step.Id, new StepConfiguration("img", $"run {name}", [])).DiscardResult();
                _byName[name] = step;
            }
        }

        public ProcessingStep this[string name] => _byName[name];

        /// <summary>Runs a production build; on success the cascade promotes it into the first step.</summary>
        public Id<ArtifactBundle> Build(bool succeed = true)
        {
            var bundleId = Id.New<ArtifactBundle>();
            var group = Groups.CreateProductionGroup(PipelineId, bundleId);
            var a = Pick(Jobs.CreateProductionJob(PipelineId, group.Id, Id.New<ProductionStep>()));
            var b = Pick(Jobs.CreateProductionJob(PipelineId, group.Id, Id.New<ProductionStep>()));
            Finish(a.Id, true);
            Finish(b.Id, succeed);
            return bundleId;
        }

        public ProcessingJob[] JobsFor(string step)
            => Jobs.ListJobs().OfType<ProcessingJob>().Where(j => j.ProcessingStepId == this[step].Id)
                .OrderBy(j => j.CreatedAt).ToArray();

        public ProcessingJob[] ActiveFor(string step)
            => JobsFor(step).Where(j => j.Status is Scheduled or InProgress).ToArray();

        /// <summary>Finishes the step's single active job.</summary>
        public Id<ArtifactBundle> Run(string step, bool succeed = true)
        {
            var active = ActiveFor(step);
            if (active.Length != 1)
                throw new InvalidOperationException($"Expected exactly one active job for '{step}', found {active.Length}.");
            Finish(active[0].Id, succeed);
            return active[0].ArtifactBundleId;
        }

        /// <summary>Runs every step in order for whatever the cascade schedules, until something stops it.</summary>
        public void RunChain()
        {
            bool progressed;
            do
            {
                progressed = false;
                foreach (var name in _byName.Keys)
                {
                    if (ActiveFor(name).Length == 1)
                    {
                        Run(name);
                        progressed = true;
                    }
                }
            } while (progressed);
        }

        /// <summary>A manual <c>trigger/processing</c>-style run of a given bundle on a step (bypasses the policy).</summary>
        public void ManualRun(string step, Id<ArtifactBundle> bundleId, bool succeed = true)
        {
            var group = Jobs.CreateProcessingRun(PipelineId, bundleId, this[step].Id);
            var job = Jobs.GetJobsByGroup(group.Id).Single();
            Finish(job.Id, succeed);
        }

        public Id<ArtifactBundle>? LastSuccess(string step)
            => JobsFor(step).Where(j => j.Status is Done).OrderBy(j => ((Done)j.Status).CompletedAt).LastOrDefault()?.ArtifactBundleId;

        private void Finish(Id<Job> jobId, bool succeed)
        {
            var started = _time.GetUtcNow();
            Jobs.UpdateJob<Job>(jobId, j => j with { Status = new InProgress(started) }).DiscardResult();
            var ended = _time.GetUtcNow();
            Jobs.UpdateJob<Job>(jobId, j => j with
            {
                Status = succeed ? new Done(started, ended) : new Failed(started, ended),
            }).DiscardResult();
        }
    }

    private static T Pick<T>(Result<T> result)
    {
        if (result.TryPickProblems(out var problems, out var value))
            throw new InvalidOperationException(string.Join("; ", problems.Select(p => p.Message)));
        return value!;
    }

    private static string Problems<T>(Result<T> result)
        => result.TryPickProblems(out var problems, out _) ? string.Join("; ", problems.Select(p => p.Message)) : "";

    private static Harness ReleasedV1(out Id<ArtifactBundle> v1)
    {
        var h = new Harness("deploy-beta", "test-after-beta", "deploy");
        v1 = h.Build();
        h.RunChain();
        return h;
    }

    // ---- Issue #41 ----

    [Test]
    public async Task StagedRelease_UnblockingProd_ReleasesTheHeldBuild()
    {
        var h = ReleasedV1(out var v1);
        await Assert.That(h.LastSuccess("deploy")).IsEqualTo(v1);

        await Assert.That(h.Promotions.SetBlocked(h["deploy"].Id, true)).Succeeded();
        var v2 = h.Build();
        h.RunChain();

        // Held at the brake: beta and its test passed v2, prod did not run.
        await Assert.That(h.LastSuccess("test-after-beta")).IsEqualTo(v2);
        await Assert.That(h.ActiveFor("deploy")).IsEmpty();

        var state = Pick(h.Promotions.SetBlocked(h["deploy"].Id, false));

        await Assert.That(state.Blocked).IsFalse();
        await Assert.That(state.Promotion!.Started).IsTrue();
        await Assert.That(state.Promotion.Bundle!.ArtifactBundleId).IsEqualTo(v2);
        await Assert.That(state.Promotion.JobGroupId).IsNotNull();
        await Assert.That(h.ActiveFor("deploy").Single().ArtifactBundleId).IsEqualTo(v2);

        h.RunChain();
        await Assert.That(h.LastSuccess("deploy")).IsEqualTo(v2);
    }

    [Test]
    public async Task Promote_OnProd_UsesWhatPassedThePreviousStep_NotItsOwnLastBundle()
    {
        var h = ReleasedV1(out var v1);
        h.Gate.Block(h["deploy"].Id);
        var v2 = h.Build();
        h.RunChain();
        h.Gate.Unblock(h["deploy"].Id); // plain gate change, as before this fix: nothing is started

        var result = Pick(h.Promotions.Promote(h["deploy"].Id, expectedBundleId: null));

        await Assert.That(result.Bundle.ArtifactBundleId).IsEqualTo(v2);
        await Assert.That(result.Bundle.ArtifactBundleId).IsNotEqualTo(v1);
        await Assert.That(result.Source.Name).IsEqualTo("test-after-beta");
        await Assert.That(h.ActiveFor("deploy").Single().ArtifactBundleId).IsEqualTo(v2);
    }

    [Test]
    public async Task Promote_OnBlockedStep_IsRefused_AndStartsNothing()
    {
        var h = ReleasedV1(out _);
        h.Gate.Block(h["deploy"].Id);
        h.Build();
        h.RunChain();
        var before = h.JobsFor("deploy").Length;

        var result = h.Promotions.Promote(h["deploy"].Id, expectedBundleId: null);

        await Assert.That(result).Failed();
        await Assert.That(Problems(result)).Contains("blocked");
        await Assert.That(h.JobsFor("deploy").Length).IsEqualTo(before);
    }

    [Test]
    public async Task Promote_RetriesAFailedDeploy_WithTheSameBundle()
    {
        var h = ReleasedV1(out _);
        var v2 = h.Build();
        h.Run("deploy-beta");
        h.Run("test-after-beta");
        h.Run("deploy", succeed: false); // flaky prod deploy

        var result = Pick(h.Promotions.Promote(h["deploy"].Id, expectedBundleId: null));

        await Assert.That(result.Bundle.ArtifactBundleId).IsEqualTo(v2);
    }

    [Test]
    public async Task Promote_SucceededStep_RerunsSameBundle_ThenCascadeContinues()
    {
        var h = ReleasedV1(out var v1);

        var result = Pick(h.Promotions.Promote(h["test-after-beta"].Id, expectedBundleId: null));
        await Assert.That(result.Bundle.ArtifactBundleId).IsEqualTo(v1);

        h.Run("test-after-beta");
        // The cascade carries on to the next step with the same bundle, as after any run.
        await Assert.That(h.ActiveFor("deploy").Single().ArtifactBundleId).IsEqualTo(v1);
    }

    [Test]
    public async Task FirstStep_PromoteUsesLatestCompletedProductionRun_IgnoringFailedBuilds()
    {
        var h = new Harness("deploy-beta", "deploy");
        h.Gate.Block(h["deploy-beta"].Id);
        var v1 = h.Build();
        h.Build(succeed: false);
        h.Gate.Unblock(h["deploy-beta"].Id);

        var state = Pick(h.Promotions.GetState(h["deploy-beta"].Id));
        await Assert.That(state.Source.IsProduction).IsTrue();
        await Assert.That(state.Promote.Bundle!.ArtifactBundleId).IsEqualTo(v1);

        var result = Pick(h.Promotions.Promote(h["deploy-beta"].Id, expectedBundleId: null));
        await Assert.That(result.Bundle.ArtifactBundleId).IsEqualTo(v1);
    }

    [Test]
    public async Task NewerBuildFailedAtPreviousStep_UnblockUsesTheOlderSuccess()
    {
        var h = ReleasedV1(out _);
        h.Gate.Block(h["deploy"].Id);
        var v2 = h.Build();
        h.RunChain(); // v2 passes beta + test, held
        var v3 = h.Build();
        h.Run("deploy-beta");
        h.Run("test-after-beta", succeed: false); // v3 fails the beta test

        var state = Pick(h.Promotions.SetBlocked(h["deploy"].Id, false));

        await Assert.That(state.Promotion!.Started).IsTrue();
        await Assert.That(state.Promotion.Bundle!.ArtifactBundleId).IsEqualTo(v2);
        await Assert.That(h.JobsFor("deploy").Any(j => j.ArtifactBundleId == v3)).IsFalse();
    }

    // ---- Unblock edge cases ----

    [Test]
    public async Task Unblock_NothingEverPassedThePreviousStep_StartsNothing()
    {
        var h = new Harness("deploy-beta", "test-after-beta", "deploy");
        h.Gate.Block(h["deploy"].Id);

        var state = Pick(h.Promotions.SetBlocked(h["deploy"].Id, false));

        await Assert.That(state.Blocked).IsFalse();
        await Assert.That(state.Promotion!.Started).IsFalse();
        await Assert.That(state.Promotion.Reason).IsEqualTo("nothingPassedSource");
        await Assert.That(h.JobsFor("deploy")).IsEmpty();
    }

    [Test]
    public async Task Unblock_StepAlreadyRanThatBundle_StartsNothing()
    {
        var h = ReleasedV1(out _);
        h.Gate.Block(h["deploy"].Id);
        var before = h.JobsFor("deploy").Length;

        var state = Pick(h.Promotions.SetBlocked(h["deploy"].Id, false));

        await Assert.That(state.Promotion!.Started).IsFalse();
        await Assert.That(state.Promotion.Reason).IsEqualTo("alreadyRan");
        await Assert.That(h.JobsFor("deploy").Length).IsEqualTo(before);
    }

    [Test]
    public async Task Unblock_JobAlreadyScheduledForStep_StartsNoSecondJob()
    {
        var h = ReleasedV1(out _);
        var v2 = h.Build();
        h.Run("deploy-beta");
        h.Run("test-after-beta"); // the cascade schedules deploy with v2
        h.Gate.Block(h["deploy"].Id); // braked after the job was already queued

        var state = Pick(h.Promotions.SetBlocked(h["deploy"].Id, false));

        await Assert.That(state.Promotion!.Started).IsFalse();
        await Assert.That(state.Promotion.Reason).IsEqualTo("jobActive");
        await Assert.That(h.ActiveFor("deploy").Single().ArtifactBundleId).IsEqualTo(v2);
    }

    [Test]
    public async Task Unblock_JobInProgressForStep_StartsNoSecondJob()
    {
        var h = ReleasedV1(out _);
        h.Build();
        h.Run("deploy-beta");
        h.Run("test-after-beta");
        var running = h.ActiveFor("deploy").Single();
        h.Jobs.UpdateJob<Job>(running.Id, j => j with { Status = new InProgress(DateTimeOffset.UtcNow) }).DiscardResult();
        h.Gate.Block(h["deploy"].Id);

        var state = Pick(h.Promotions.SetBlocked(h["deploy"].Id, false));
        var promote = h.Promotions.Promote(h["deploy"].Id, expectedBundleId: null);

        await Assert.That(state.Promotion!.Reason).IsEqualTo("jobActive");
        await Assert.That(promote).Failed();
        await Assert.That(h.ActiveFor("deploy")).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Unblock_PreviousStepsSuccessIsOlder_NeverDowngrades()
    {
        var h = ReleasedV1(out var v1);
        var v2 = h.Build();
        h.RunChain(); // v2 everywhere
        h.Gate.Block(h["deploy"].Id);
        h.ManualRun("test-after-beta", v1); // someone re-tests the older build on beta

        var state = Pick(h.Promotions.SetBlocked(h["deploy"].Id, false));
        var promote = h.Promotions.Promote(h["deploy"].Id, expectedBundleId: null);

        await Assert.That(state.Promotion!.Started).IsFalse();
        await Assert.That(state.Promotion.Reason).IsEqualTo("wouldDowngrade");
        await Assert.That(promote).Failed();
        await Assert.That(Problems(promote)).Contains("never goes backwards");
        await Assert.That(h.ActiveFor("deploy")).IsEmpty();
        await Assert.That(h.LastSuccess("deploy")).IsEqualTo(v2);
    }

    [Test]
    public async Task Unblock_BundleNoLongerExists_StartsNothing_AndPromoteFailsClearly()
    {
        var h = ReleasedV1(out _);
        h.Gate.Block(h["deploy"].Id);
        var unknown = Id.New<ArtifactBundle>(); // no production run on record for it
        h.ManualRun("test-after-beta", unknown);

        var state = Pick(h.Promotions.SetBlocked(h["deploy"].Id, false));
        var promote = h.Promotions.Promote(h["deploy"].Id, expectedBundleId: null);

        await Assert.That(state.Promotion!.Started).IsFalse();
        await Assert.That(state.Promotion.Reason).IsEqualTo("bundleGone");
        await Assert.That(promote).Failed();
        await Assert.That(Problems(promote)).Contains("no longer available");
        await Assert.That(h.ActiveFor("deploy")).IsEmpty();
    }

    [Test]
    public async Task Unblock_SeveralBlockedInARow_DoesNotSkipTheLaterBlockedStep()
    {
        var h = new Harness("deploy-beta", "test-after-beta", "deploy", "publish-cli");
        h.Build();
        h.RunChain();
        h.Gate.Block(h["deploy"].Id);
        h.Gate.Block(h["publish-cli"].Id);
        var v2 = h.Build();
        h.RunChain();

        var state = Pick(h.Promotions.SetBlocked(h["deploy"].Id, false));
        await Assert.That(state.Promotion!.Bundle!.ArtifactBundleId).IsEqualTo(v2);
        h.Run("deploy");

        // publish-cli is still braked: the cascade stops there, nothing skips past it.
        await Assert.That(h.ActiveFor("publish-cli")).IsEmpty();
        var publish = Pick(h.Promotions.GetState(h["publish-cli"].Id));
        await Assert.That(publish.Promote.Reason).IsEqualTo("blocked");
        await Assert.That(publish.OnUnblock!.Bundle!.ArtifactBundleId).IsEqualTo(v2);
    }

    [Test]
    public async Task Unblock_LaterStepWhileEarlierStillBlocked_OnlyGetsWhatPassedTheEarlierOne()
    {
        var h = ReleasedV1(out var v1);
        h.Gate.Block(h["test-after-beta"].Id);
        h.Gate.Block(h["deploy"].Id);
        h.Build();
        h.RunChain(); // the new build only reaches deploy-beta

        var state = Pick(h.Promotions.SetBlocked(h["deploy"].Id, false));

        // deploy's source is test-after-beta, whose last success is still v1 — which deploy already ran.
        await Assert.That(state.Promotion!.Started).IsFalse();
        await Assert.That(state.Promotion.Reason).IsEqualTo("alreadyRan");
        await Assert.That(h.ActiveFor("deploy")).IsEmpty();
        await Assert.That(h.LastSuccess("deploy")).IsEqualTo(v1);
    }

    [Test]
    public async Task Unblock_CalledTwice_StartsExactlyOnePromotion()
    {
        var h = ReleasedV1(out _);
        h.Gate.Block(h["deploy"].Id);
        var v2 = h.Build();
        h.RunChain();

        var first = Pick(h.Promotions.SetBlocked(h["deploy"].Id, false));
        var second = Pick(h.Promotions.SetBlocked(h["deploy"].Id, false));

        await Assert.That(first.Promotion!.Started).IsTrue();
        await Assert.That(second.Promotion!.Started).IsFalse();
        await Assert.That(second.Promotion.Reason).IsEqualTo("notBlocked");
        await Assert.That(h.JobsFor("deploy").Count(j => j.ArtifactBundleId == v2)).IsEqualTo(1);
    }

    [Test]
    public async Task Unblock_AStepThatWasNotBlocked_RunsNoCheck()
    {
        var h = ReleasedV1(out var v1);
        h.Gate.Block(h["deploy"].Id);
        h.ManualRun("test-after-beta", h.Build()); // a newer success at test-after-beta…
        h.RunChain();
        h.Gate.Unblock(h["deploy"].Id); // …but deploy is open again by a plain gate change

        var state = Pick(h.Promotions.SetBlocked(h["deploy"].Id, false));

        await Assert.That(state.Promotion!.Reason).IsEqualTo("notBlocked");
        await Assert.That(h.ActiveFor("deploy")).IsEmpty();
        await Assert.That(h.LastSuccess("deploy")).IsEqualTo(v1);
    }

    [Test]
    public async Task Block_ReturnsBlocked_WithNoPromotion()
    {
        var h = ReleasedV1(out _);

        var state = Pick(h.Promotions.SetBlocked(h["deploy"].Id, true));

        await Assert.That(state.Blocked).IsTrue();
        await Assert.That(state.Promotion).IsNull();
        await Assert.That(h.Gate.IsBlocked(h["deploy"].Id)).IsTrue();
    }

    // ---- Promote guards ----

    [Test]
    public async Task Promote_ExpectedBundleNoLongerCurrent_IsRefused()
    {
        var h = ReleasedV1(out _);
        h.Gate.Block(h["deploy"].Id);
        var v2 = h.Build();
        h.RunChain();
        h.Gate.Unblock(h["deploy"].Id);
        var shown = Pick(h.Promotions.GetState(h["deploy"].Id)).Promote.Bundle!.ArtifactBundleId;
        await Assert.That(shown).IsEqualTo(v2);

        // A newer build reaches test-after-beta between render and click (deploy is busy meanwhile).
        h.Gate.Block(h["deploy"].Id);
        var v3 = h.Build();
        h.RunChain();
        h.Gate.Unblock(h["deploy"].Id);

        var stale = h.Promotions.Promote(h["deploy"].Id, shown);
        await Assert.That(stale).Failed();
        await Assert.That(Problems(stale)).Contains("Refresh");
        await Assert.That(h.ActiveFor("deploy")).IsEmpty();

        var current = Pick(h.Promotions.Promote(h["deploy"].Id, v3));
        await Assert.That(current.Bundle.ArtifactBundleId).IsEqualTo(v3);
    }

    [Test]
    public async Task Promote_NothingPassedThePreviousStep_IsRefused()
    {
        var h = new Harness("deploy-beta", "deploy");

        var result = h.Promotions.Promote(h["deploy"].Id, expectedBundleId: null);

        await Assert.That(result).Failed();
        await Assert.That(Problems(result)).Contains("nothing has succeeded at 'deploy-beta'");
    }

    [Test]
    public async Task Promote_FirstStep_NoProductionRunYet_IsRefused()
    {
        var h = new Harness("deploy-beta", "deploy");

        var result = h.Promotions.Promote(h["deploy-beta"].Id, expectedBundleId: null);

        await Assert.That(result).Failed();
        await Assert.That(Problems(result)).Contains("no production run has completed");
    }

    [Test]
    public async Task Promote_UnconfiguredStep_IsRefused()
    {
        var h = new Harness("deploy-beta", "deploy:unconfigured");
        h.Build();
        h.RunChain();

        var result = h.Promotions.Promote(h["deploy"].Id, expectedBundleId: null);

        await Assert.That(result).Failed();
        await Assert.That(Problems(result)).Contains("no configuration");
    }

    [Test]
    public async Task Promote_UnknownStep_IsRefused()
    {
        var h = new Harness("deploy");

        await Assert.That(h.Promotions.Promote(Id.New<ProcessingStep>(), expectedBundleId: null)).Failed();
    }

    // ---- Races with the cascade ----

    [Test]
    public async Task UnblockThenCascadeForTheSameBundle_SchedulesOneJob()
    {
        var h = ReleasedV1(out _);
        h.Gate.Block(h["deploy"].Id);
        var v2 = h.Build();
        h.RunChain();
        var testGroup = h.JobsFor("test-after-beta").Last().JobGroupId;

        Pick(h.Promotions.SetBlocked(h["deploy"].Id, false));
        // The cascade for test-after-beta's v2 run arrives late (it lost the race to the unblock).
        h.Cascade.HandleGroupCompleted(testGroup);

        await Assert.That(h.JobsFor("deploy").Count(j => j.ArtifactBundleId == v2)).IsEqualTo(1);
    }

    [Test]
    public async Task ConcurrentUnblockAndPromote_StartAtMostOneJob()
    {
        for (var round = 0; round < 25; round++)
        {
            var h = ReleasedV1(out _);
            h.Gate.Block(h["deploy"].Id);
            var v2 = h.Build();
            h.RunChain();

            var gate = new Barrier(3);
            var unblock = Task.Run(() => { gate.SignalAndWait(); h.Promotions.SetBlocked(h["deploy"].Id, false).DiscardResult(); });
            var promote = Task.Run(() => { gate.SignalAndWait(); h.Promotions.Promote(h["deploy"].Id, null).DiscardResult(); });
            var unblock2 = Task.Run(() => { gate.SignalAndWait(); h.Promotions.SetBlocked(h["deploy"].Id, false).DiscardResult(); });
            await Task.WhenAll(unblock, promote, unblock2);

            var deployJobs = h.JobsFor("deploy").Where(j => j.ArtifactBundleId == v2).ToArray();
            await Assert.That(deployJobs.Length).IsLessThanOrEqualTo(1);
        }
    }

    // ---- Read model ----

    [Test]
    public async Task ListStates_NamesTheBundleEachStepWouldPromote()
    {
        var h = ReleasedV1(out var v1);
        h.Gate.Block(h["deploy"].Id);
        var v2 = h.Build();
        h.RunChain();

        var states = Pick(h.Promotions.ListStates(h.PipelineId)).ToDictionary(s => s.StepName);

        await Assert.That(states["deploy"].Blocked).IsTrue();
        await Assert.That(states["deploy"].Source.Name).IsEqualTo("test-after-beta");
        await Assert.That(states["deploy"].SourceLastSuccess!.ArtifactBundleId).IsEqualTo(v2);
        await Assert.That(states["deploy"].LastSuccess!.ArtifactBundleId).IsEqualTo(v1);
        await Assert.That(states["deploy"].Promote.Allowed).IsFalse();
        await Assert.That(states["deploy"].Promote.Reason).IsEqualTo("blocked");
        await Assert.That(states["deploy"].OnUnblock!.Allowed).IsTrue();
        await Assert.That(states["deploy"].OnUnblock!.Bundle!.ArtifactBundleId).IsEqualTo(v2);

        await Assert.That(states["deploy-beta"].Source.IsProduction).IsTrue();
        await Assert.That(states["deploy-beta"].Promote.Bundle!.ArtifactBundleId).IsEqualTo(v2);
        await Assert.That(states["deploy-beta"].OnUnblock).IsNull();

        // What the read model names is what Promote then uses.
        var beta = Pick(h.Promotions.Promote(h["deploy-beta"].Id, states["deploy-beta"].Promote.Bundle!.ArtifactBundleId));
        await Assert.That(beta.Bundle).IsEqualTo(states["deploy-beta"].Promote.Bundle!);
    }

    [Test]
    public async Task BundlesInUse_CoversEveryStepsLastSuccessAndPendingJobs()
    {
        var h = ReleasedV1(out var v1);
        h.Gate.Block(h["deploy"].Id);
        var v2 = h.Build();
        h.RunChain();
        var v3 = h.Build(); // queued at deploy-beta
        var failed = h.Build(succeed: false);

        var inUse = h.Promotions.BundlesInUse(h.PipelineId).ToHashSet();

        await Assert.That(inUse.Contains(v1)).IsTrue();   // deploy's last success
        await Assert.That(inUse.Contains(v2)).IsTrue();   // test-after-beta's last success (held)
        await Assert.That(inUse.Contains(v3)).IsTrue();   // latest production success + pending job
        await Assert.That(inUse.Contains(failed)).IsFalse();
    }
}
