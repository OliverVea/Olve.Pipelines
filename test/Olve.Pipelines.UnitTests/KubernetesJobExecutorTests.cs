using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Failures;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Shared.Storage;
using Olve.Pipelines.UnitTests.Manifests;
using Olve.Pipelines.UnitTests.Scheduling;
using Olve.Results;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.UnitTests;

public class KubernetesJobExecutorTests
{
    private static readonly HarnessContainerState InitOk = new(HarnessJobBuilder.InitContainerName, 0, "Completed", null);
    private static readonly HarnessPodState Running = new(null, null, InitOk, new HarnessContainerState("step", null, null, null));
    private static readonly HarnessPodState Exited0 = new(null, null, InitOk, new HarnessContainerState("step", 0, "Completed", null));

    private sealed class FakeKubernetes : IKubernetesClient, IHarnessKubernetesClient
    {
        /// <summary>Returned by GetHarnessPodStateAsync before any Job was created by the executor.</summary>
        public HarnessPodState? Existing { get; set; }
        /// <summary>States returned after creation (or on reattach); the last one repeats.</summary>
        public List<HarnessPodState?> States { get; } = [];
        public bool Created { get; private set; }
        public int CreateJobCalls { get; private set; }
        public int FailCreateJobTimes { get; set; }
        public K8sJobManifest? LastJob { get; private set; }
        public Dictionary<string, string>? LastSecretData { get; private set; }
        public List<string> UpsertedSecrets { get; } = [];
        public List<string> DeletedSecrets { get; } = [];
        public List<string> DeletedJobs { get; } = [];
        public Dictionary<string, string> Logs { get; } = [];
        public Action? OnPoll { get; set; }

        public Task<bool> CreateJobAsync(string ns, K8sJobManifest manifest, CancellationToken ct = default)
        {
            CreateJobCalls++;
            if (FailCreateJobTimes > 0)
            {
                FailCreateJobTimes--;
                throw new InvalidOperationException("simulated K8s API unavailable");
            }
            LastJob = manifest;
            Created = true;
            return Task.FromResult(true);
        }

        public Task UpsertSecretAsync(string ns, string secretName, Dictionary<string, string> data, CancellationToken ct = default)
        {
            UpsertedSecrets.Add(secretName);
            LastSecretData = data;
            return Task.CompletedTask;
        }

        public Task<HarnessPodState?> GetHarnessPodStateAsync(string ns, string jobName, CancellationToken ct = default)
        {
            if (!Created)
            {
                if (Existing is null)
                    return Task.FromResult<HarnessPodState?>(null);
                Created = true; // reattached: later polls follow States
                return Task.FromResult<HarnessPodState?>(Existing);
            }
            OnPoll?.Invoke();
            if (States.Count == 0)
                return Task.FromResult<HarnessPodState?>(Exited0);
            var next = States[0];
            if (States.Count > 1) States.RemoveAt(0);
            return Task.FromResult(next);
        }

        public Task<string?> GetPodLogsAsync(string ns, string jobName, string? container = null, CancellationToken ct = default)
            => Logs.TryGetValue(container ?? "", out var log) ? Task.FromResult<string?>(log) : throw new HttpRequestException("container not started");

        public Task DeleteJobAsync(string ns, string jobName, CancellationToken ct = default)
        {
            DeletedJobs.Add(jobName);
            return Task.CompletedTask;
        }

        public Task DeleteSecretAsync(string ns, string secretName, CancellationToken ct = default)
        {
            DeletedSecrets.Add(secretName);
            return Task.CompletedTask;
        }

        public Task<KubernetesJobStatus> GetJobStatusAsync(string ns, string jobName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<KubernetesJobStatus?> TryGetJobStatusAsync(string ns, string jobName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CreateSecretAsync(string ns, string secretName, Dictionary<string, string> data, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Dictionary<string, string>?> GetSecretAsync(string ns, string secretName, CancellationToken ct = default) => Task.FromResult<Dictionary<string, string>?>(null);
        public Task UpdateSecretAsync(string ns, string secretName, Dictionary<string, string> data, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeStorage : IHarnessStorage
    {
        public ConcurrentDictionary<string, string> Objects { get; } = new();
        public List<string> Deleted { get; } = [];
        public HarnessCollection Collection { get; set; } = new(
            new HarnessResultContents(new HarnessEnvelope.Succeeded("""{"lines":3}"""), "{}", []), []);

        public Task PutTextAsync(string key, string content, string contentType, CancellationToken ct = default)
        {
            Objects[key] = content;
            return Task.CompletedTask;
        }

        public Task<string?> GetTextAsync(string key, CancellationToken ct = default)
            => Task.FromResult(Objects.TryGetValue(key, out var v) ? v : null);

        public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(Objects.ContainsKey(key));

        public Task CopyAsync(string sourceKey, string destinationKey, CancellationToken ct = default)
        {
            Objects[destinationKey] = Objects[sourceKey];
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            Deleted.Add(key);
            Objects.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task<HarnessCollection> CollectAsync(Attempt attempt, CancellationToken ct = default) => Task.FromResult(Collection);
    }

    private sealed class StubS3CredentialsProvider : ICredentialsProvider<S3Credentials>
    {
        public Task<S3Credentials> GetCredentialsAsync(CancellationToken ct = default)
            => Task.FromResult(new S3Credentials("access", "secret"));
    }

    private sealed class FakeApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
    }

    private sealed record Harness(
        KubernetesJobExecutor Executor,
        FakeKubernetes K8s,
        FakeStorage Storage,
        SchedulerScenario Scenario,
        Run Run,
        JobWatcherRegistry Registry,
        FakeApplicationLifetime Lifetime,
        HarnessJobFactory Factory,
        HarnessJobControl Control)
    {
        public Attempt Hello => Scenario.Latest(Scenario.Get(Run), "hello")!;
        public AttemptService Attempts => Scenario.D.AttemptService;
    }

    private static Harness Build(string input = """{"sha":"abcdef1","mode":"normal"}""")
    {
        var k8s = new FakeKubernetes();
        var storage = new FakeStorage();
        var scenario = new SchedulerScenario(TestFixtureManifest.Text);
        var run = scenario.Start(input);

        var registry = new JobWatcherRegistry(NullLogger<JobWatcherRegistry>.Instance);
        var lifetime = new FakeApplicationLifetime();
        var presigner = new S3UrlPresigner(
            new PresignOptions(new Uri("http://garage.test:3900"), "bucket", "us-east-1", TimeSpan.FromHours(1)),
            new StubS3CredentialsProvider(), TimeProvider.System);
        var factory = new HarnessJobFactory(presigner, new HarnessOptions());
        var control = new HarnessJobControl(k8s, k8s, new KubernetesOptions("runners"),
            TimeProvider.System, NullLogger<HarnessJobControl>.Instance) { FatalWaitingGrace = TimeSpan.Zero };

        KubernetesJobExecutor? executor = null;
        var services = new ServiceCollection();
        services.AddSingleton(scenario.D.AttemptService);
        services.AddTransient<IJobExecutor>(_ => executor!);
        var sp = services.BuildServiceProvider();

        executor = new KubernetesJobExecutor(
            sp.GetRequiredService<IServiceScopeFactory>(), registry, lifetime, control, factory, storage,
            scenario.D.AttemptService, scenario.D.Launches, NullLogger<KubernetesJobExecutor>.Instance)
        {
            SubmissionRetryDelay = TimeSpan.Zero,
            PollInterval = TimeSpan.FromMilliseconds(5),
        };

        return new Harness(executor, k8s, storage, scenario, run, registry, lifetime, factory, control);
    }

    private static async Task RunWatcher(Harness h, Id<Attempt> id)
    {
        h.Executor.EnsureRunning(id);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (h.Registry.IsRunning(id))
        {
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException($"Watcher for '{id}' did not complete in time.");
            await Task.Delay(10);
        }
    }

    [Test]
    public async Task FreshSubmit_UploadsInput_CreatesSecretThenJob_CollectsAndSucceeds()
    {
        var h = Build();
        h.K8s.States.Add(Running);
        h.K8s.States.Add(Exited0);
        h.K8s.Logs["step"] = "hi\n---OLVE-RESULT-BEGIN---\n{\"status\":\"succeeded\",\"output\":{}}\n---OLVE-RESULT-END---\n";
        h.Storage.Collection = new(new HarnessResultContents(new HarnessEnvelope.Succeeded("""{"message":"m"}"""), "{}", []), []);
        var attempt = h.Hello;

        await RunWatcher(h, attempt.Id);

        var after = h.Scenario.D.Get(attempt.Id);
        await Assert.That(after.Status).IsTypeOf<Succeeded>();
        await Assert.That(after.Output!.Json).IsEqualTo("""{"message":"m"}""");
        await Assert.That(h.K8s.CreateJobCalls).IsEqualTo(1);
        await Assert.That(h.K8s.UpsertedSecrets).IsEquivalentTo(new[] { HarnessStorageKeys.UrlSecretName(attempt.Id) });
        await Assert.That(h.K8s.DeletedSecrets).IsEquivalentTo(new[] { HarnessStorageKeys.UrlSecretName(attempt.Id) });
        await Assert.That(h.Storage.Objects[HarnessStorageKeys.AttemptInputKey(attempt)]).IsEqualTo(attempt.Input!);
        await Assert.That(h.Storage.Objects[StorageLayout.AttemptLogKey(attempt)]).StartsWith("hi\n");
        await Assert.That(h.Storage.Deleted).Contains(HarnessStorageKeys.AttemptResultArchiveKey(attempt));
    }

    [Test]
    public async Task FreshSubmit_JobIsTheHarnessSpecOfTheAttempt()
    {
        var h = Build();
        var attempt = h.Hello;

        await RunWatcher(h, attempt.Id);

        var job = h.K8s.LastJob!;
        await Assert.That(job.Metadata.Name).IsEqualTo(attempt.KubernetesJobName);
        var step = job.Spec.Template.Spec.Containers.Single();
        await Assert.That(step.Image).IsEqualTo("alpine:3.22");
        await Assert.That(step.Command!.TakeLast(2)).IsEquivalentTo(new[] { "run", "hello" });
        await Assert.That(job.Spec.ActiveDeadlineSeconds).IsEqualTo(300 + new HarnessOptions().StartupGraceSeconds);
        await Assert.That(h.K8s.LastSecretData![HarnessJobBuilder.StepUrlKey])
            .Contains(StorageLayout.VersionBinaryKey(attempt.PipelineId, h.Scenario.Version.Number));
    }

    [Test]
    public async Task SkippedEnvelope_MarksSkipped()
    {
        var h = Build();
        h.Storage.Collection = new(new HarnessResultContents(new HarnessEnvelope.Skipped("nothing to do"), "{}", []), []);

        await RunWatcher(h, h.Hello.Id);

        await Assert.That(h.Hello.Status).IsTypeOf<Skipped>();
        await Assert.That(((Skipped)h.Hello.Status).Reason).IsEqualTo("nothing to do");
    }

    [Test]
    public async Task InitFailure_FailsWithInitLog()
    {
        var h = Build();
        h.K8s.States.Add(new HarnessPodState(null, null,
            new HarnessContainerState("olve-init", 65, "Error", null), new HarnessContainerState("step", null, "PodInitializing", null)));
        h.K8s.Logs["olve-init"] = "olve-init: HTTP/1.1 403 Forbidden\n";
        h.Storage.Collection = new(null, []);
        var attempt = h.Hello;

        await RunWatcher(h, attempt.Id);

        var failed = (Failed)h.Hello.Status;
        await Assert.That(failed.Reason!).Contains("olve-init failed");
        await Assert.That(h.Storage.Objects[StorageLayout.AttemptLogKey(attempt)]).Contains("403 Forbidden");
    }

    [Test]
    public async Task StuckContainer_FailsAndDeletesTheJob()
    {
        var h = Build();
        h.K8s.States.Add(new HarnessPodState(null, null, InitOk,
            new HarnessContainerState("step", null, "CreateContainerConfigError", "secret key FOO not found")));
        h.Storage.Collection = new(null, []);
        var attempt = h.Hello;

        await RunWatcher(h, attempt.Id);

        var failed = (Failed)h.Hello.Status;
        await Assert.That(failed.Reason!).Contains("CreateContainerConfigError");
        await Assert.That(h.K8s.DeletedJobs).Contains(attempt.KubernetesJobName);
    }

    [Test]
    public async Task CancelledWhileInProgress_DeletesTheJob()
    {
        var h = Build();
        var attempt = h.Hello;
        var polls = 0;
        h.K8s.States.Add(Running);
        h.K8s.OnPoll = () => { if (++polls == 2) h.Attempts.Cancel(attempt.Id).DiscardResult(); };

        await RunWatcher(h, attempt.Id);

        await Assert.That(h.Hello.Status).IsTypeOf<Cancelled>();
        await Assert.That(h.K8s.DeletedJobs).Contains(attempt.KubernetesJobName);
        await Assert.That(h.K8s.DeletedSecrets).Contains(HarnessStorageKeys.UrlSecretName(attempt.Id));
    }

    [Test]
    public async Task Reattach_ExistingJob_DoesNotSubmitAgain()
    {
        var h = Build();
        h.K8s.Existing = Running;
        var attempt = h.Hello;

        await RunWatcher(h, attempt.Id);

        await Assert.That(h.K8s.CreateJobCalls).IsEqualTo(0);
        await Assert.That(h.K8s.UpsertedSecrets).IsEmpty();
        await Assert.That(h.Hello.Status).IsTypeOf<Succeeded>();
    }

    [Test]
    public async Task Reattach_InProgressAttempt_KeepsOriginalStartTime()
    {
        var h = Build();
        h.K8s.Existing = Running;
        var attempt = h.Hello;
        var originalStart = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await Assert.That(h.Attempts.Update(attempt.Id, a => a with { Status = new InProgress(originalStart) })).Succeeded();

        await RunWatcher(h, attempt.Id);

        await Assert.That(((Succeeded)h.Hello.Status).StartedAt).IsEqualTo(originalStart);
    }

    [Test]
    public async Task InProgressAttempt_WhoseJobIsGone_FailsWithoutResubmitting()
    {
        var h = Build();
        var attempt = h.Hello;
        await Assert.That(h.Attempts.MarkInProgress(attempt.Id)).Succeeded();

        await RunWatcher(h, attempt.Id);

        await Assert.That(h.K8s.CreateJobCalls).IsEqualTo(0);
        await Assert.That(((Failed)h.Hello.Status).Reason!).Contains("no longer exists");
    }

    // A controller restart (e.g. v2's own self-deploy) cancels the watcher while the Job runs: the URL
    // Secret must survive so the reattach finds working URLs, and the attempt stays InProgress.
    [Test]
    public async Task ShutdownDuringWatch_PreservesUrlSecret()
    {
        var h = Build();
        h.K8s.Existing = Running;
        h.K8s.States.Add(Running);
        h.K8s.OnPoll = h.Lifetime.StopApplication;
        var attempt = h.Hello;

        await RunWatcher(h, attempt.Id);

        await Assert.That(h.K8s.DeletedSecrets).IsEmpty();
        await Assert.That(h.Hello.Status).IsTypeOf<InProgress>();
    }

    [Test]
    public async Task SubmissionKeepsFailing_MarksFailedAfterMaxAttempts()
    {
        var h = Build();
        h.K8s.FailCreateJobTimes = 99;
        var attempt = h.Hello;

        for (var i = 0; i < KubernetesJobExecutor.MaxSubmissionAttempts; i++)
            await RunWatcher(h, attempt.Id);

        await Assert.That(h.Hello.Status).IsTypeOf<Failed>();
        await Assert.That(h.K8s.CreateJobCalls).IsEqualTo(KubernetesJobExecutor.MaxSubmissionAttempts);
        await Assert.That(h.K8s.DeletedSecrets).Contains(HarnessStorageKeys.UrlSecretName(attempt.Id));
        await Assert.That(h.Storage.Objects[StorageLayout.AttemptLogKey(attempt)]).Contains("giving up");
    }

    [Test]
    public async Task SubmissionFailsThenRecovers_Completes()
    {
        var h = Build();
        h.K8s.FailCreateJobTimes = 1;
        var attempt = h.Hello;

        await RunWatcher(h, attempt.Id);
        await Assert.That(h.Hello.Status).IsTypeOf<Scheduled>();
        await Assert.That(((Scheduled)h.Hello.Status).SubmissionAttempts).IsEqualTo(1);

        await RunWatcher(h, attempt.Id);
        await Assert.That(h.Hello.Status).IsTypeOf<Succeeded>();
    }

    // ---- failure handlers (untracked harness Jobs) ----

    /// <summary>Fails the fixture run at echo (hello, count succeed) and plans its log-failure handler.</summary>
    private static FailureHandlerDispatch FailAndPlan(Harness h)
    {
        var s = h.Scenario;
        var run = h.Run;
        s.Succeed(run, "hello", $$"""{"file":{{SchedulerScenario.FileRef(run.Number, "hello", 1, "greeting.txt")}},"message":"m"}""");
        s.Succeed(run, "count", """{"lines":1}""");
        var echo = s.Fail(run, "echo", "handler threw: on purpose");
        var failedRun = s.Get(run);
        var (dispatches, _) = FailureHandlerPlanner.Plan(s.Pipeline, failedRun, s.Version, TestFixtureManifest.Parse(), [echo], "https://p.example");
        return dispatches.Single();
    }

    private static HarnessFailureHandlerLauncher Launcher(Harness h)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHarnessStorage>(h.Storage);
        services.AddSingleton(h.Factory);
        services.AddSingleton(h.Control);
        services.AddSingleton(new HarnessOptions());
        return new HarnessFailureHandlerLauncher(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), h.Lifetime,
            NullLogger<HarnessFailureHandlerLauncher>.Instance) { PollInterval = TimeSpan.FromMilliseconds(5) };
    }

    [Test]
    public async Task FailureHandler_RunsAsAnUntrackedHarnessJob_AndPersistsItsLog()
    {
        var h = Build("""{"sha":"abcdef1","mode":"fail"}""");
        var dispatch = FailAndPlan(h);
        var attemptsBefore = h.Attempts.List().Count;
        h.K8s.States.Add(Running);
        h.K8s.States.Add(Exited0);
        h.K8s.Logs["step"] = "run failed run=p#1\n---OLVE-RESULT-BEGIN---\n{\"status\":\"succeeded\",\"output\":null}\n---OLVE-RESULT-END---\n";

        await Launcher(h).RunAsync(dispatch, CancellationToken.None);

        await Assert.That(h.Storage.Objects[dispatch.InputKey]).IsEqualTo(dispatch.InputJson);
        var job = h.K8s.LastJob!;
        await Assert.That(job.Metadata.Name).IsEqualTo(dispatch.JobName);
        await Assert.That(job.Metadata.Labels!["olve-role"]).IsEqualTo(HarnessJobBuilder.FailureHandlerRole);
        await Assert.That(job.Metadata.Labels!["olve-pipeline"]).IsEqualTo(h.Scenario.Pipeline.Id.Value.Value.ToString("N"));
        var step = job.Spec.Template.Spec.Containers.Single();
        await Assert.That(step.Image).IsEqualTo("alpine:3.22");
        await Assert.That(step.Command!.TakeLast(2).ToArray()).IsEquivalentTo(new[] { "run", "log-failure" });
        await Assert.That(step.Env!.Select(e => e.Name)).DoesNotContain(HarnessJobBuilder.ResultUrlKey);
        await Assert.That(job.Spec.TtlSecondsAfterFinished).IsEqualTo(new HarnessOptions().TtlSecondsAfterFinished);
        await Assert.That(h.K8s.UpsertedSecrets).IsEquivalentTo(new[] { dispatch.UrlSecretName });
        await Assert.That(h.K8s.LastSecretData!.Keys.Order().ToArray()).IsEquivalentTo(new[] { HarnessJobBuilder.InputUrlKey, HarnessJobBuilder.StepUrlKey });
        await Assert.That(h.K8s.DeletedSecrets).IsEquivalentTo(new[] { dispatch.UrlSecretName });
        await Assert.That(h.Storage.Objects[dispatch.LogKey]).Contains("run failed run=p#1");
        await Assert.That(h.Storage.Objects[dispatch.LogKey]).Contains("controller: failure handler succeeded");
        // Untracked: no attempt, and the run stays Failed.
        await Assert.That(h.Attempts.List().Count).IsEqualTo(attemptsBefore);
        await Assert.That(h.Scenario.Get(h.Run).Status).IsEqualTo(RunStatus.Failed);
    }

    [Test]
    public async Task FailureHandler_ThatFails_IsLoggedOnly()
    {
        var h = Build("""{"sha":"abcdef1","mode":"fail"}""");
        var dispatch = FailAndPlan(h);
        h.K8s.States.Add(new HarnessPodState(null, null, InitOk, new HarnessContainerState("step", 1, "Error", null)));
        h.K8s.Logs["step"] = "---OLVE-RESULT-BEGIN---\n{\"status\":\"failed\",\"error\":{\"kind\":\"handler\",\"message\":\"nope\"}}\n---OLVE-RESULT-END---\n";

        await Launcher(h).RunAsync(dispatch, CancellationToken.None);

        await Assert.That(h.Storage.Objects[dispatch.LogKey]).Contains("controller: failure handler failed (handler): nope");
        await Assert.That(h.K8s.DeletedSecrets).IsEquivalentTo(new[] { dispatch.UrlSecretName });
        await Assert.That(h.Scenario.Get(h.Run).Status).IsEqualTo(RunStatus.Failed);
    }
}
