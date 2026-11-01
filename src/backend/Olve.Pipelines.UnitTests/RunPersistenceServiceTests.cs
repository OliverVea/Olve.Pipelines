using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Configs;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Shared;
using Olve.Pipelines.Shared.Persistence;
using Olve.Results;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;
using static Olve.Pipelines.Pipelines.Versions.PipelineActivation;

namespace Olve.Pipelines.UnitTests;

public class RunPersistenceServiceTests
{
    /// <summary>Key-addressed in-memory object store, so multi-object layouts are really exercised.</summary>
    private sealed class FakeObjectStore : ISnapshotStore
    {
        public ConcurrentDictionary<string, byte[]> Objects { get; } = new();
        public ConcurrentQueue<string> Writes { get; } = new();
        public ConcurrentQueue<string> Deletes { get; } = new();
        public Exception? ReadException { get; set; }
        public Func<string, bool> FailWrite { get; set; } = _ => false;

        public Task<byte[]?> TryReadAsync(string key, CancellationToken ct)
        {
            if (ReadException is not null) return Task.FromException<byte[]?>(ReadException);
            return Task.FromResult(Objects.TryGetValue(key, out var v) ? v : null);
        }

        public Task WriteAsync(string key, byte[] content, CancellationToken ct)
        {
            if (FailWrite(key)) return Task.FromException(new IOException($"simulated write failure for '{key}'"));
            Objects[key] = content;
            Writes.Enqueue(key);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken ct)
        {
            Objects.TryRemove(key, out _);
            Deletes.Enqueue(key);
            return Task.CompletedTask;
        }

        public T Read<T>(string key, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
            => JsonSerializer.Deserialize(Objects[key], typeInfo)!;
    }

    private sealed record Node(TestRunDomain Domain, RunPersistenceService Service, PersistenceReadiness Readiness);

    private static Node CreateNode(ISnapshotStore? store, StorageMode mode = StorageMode.Persistent)
    {
        var domain = new TestRunDomain();
        var readiness = new PersistenceReadiness();
        var service = new RunPersistenceService(
            domain.Versions, domain.Runs, domain.Attempts, domain.Activations, domain.Sequences, domain.BlockedSteps, domain.PendingStarts, domain.EntityVersions, domain.PollTriggers,
            domain.ConfigSnapshotStore, domain.PendingVersions,
            new StorageOptions("test-bucket", Mode: mode), readiness,
            NullLogger<RunPersistenceService>.Instance, store);
        return new Node(domain, service, readiness);
    }

    private static async Task<Node> StartNode(FakeObjectStore store)
    {
        var node = CreateNode(store);
        await node.Service.StartingAsync(CancellationToken.None);
        await node.Service.StartedAsync(CancellationToken.None);
        return node;
    }

    private static RunPersistenceJsonContext Json => RunPersistenceJsonContext.Default;

    // ---- durability ----

    [Test]
    public async Task Saved_WritesWhatChanged_WithoutWaitingForTheTimer()
    {
        var store = new FakeObjectStore();
        var node = await StartNode(store);
        var d = node.Domain;
        var run = d.CreateRun(d.AddVersion(d.CreatePipeline().Id));

        await Assert.That(await node.Service.SavedAsync()).Succeeded();

        await Assert.That(store.Objects.Keys).Contains(StorageLayout.RunStateKey(run.PipelineId, run.Number));
    }

    // A false success here is a lost run after an acknowledged start.
    [Test]
    [Arguments("run")]
    [Arguments(RunPersistenceService.StateKey)]
    [Arguments(RunPersistenceService.RunsIndexKey)]
    public async Task Saved_FailsWhenAnyWriteFails_AndSucceedsOnceStorageIsBack(string failing)
    {
        const string unsaved = "The change was made but could not be saved; it may be lost if the server restarts.";
        var store = new FakeObjectStore();
        var node = await StartNode(store);
        var d = node.Domain;
        var version = d.AddVersion(d.CreatePipeline().Id);
        await Assert.That(await node.Service.SavedAsync()).Succeeded();
        store.FailWrite = key => failing == "run" ? key.EndsWith("/run.json", StringComparison.Ordinal) : key == failing;
        d.CreateRun(version);

        var failed = await node.Service.SavedAsync();

        await Assert.That(failed.TryPickProblems(out var problems) ? problems.Single().FormattedMessage : "").IsEqualTo(unsaved);
        store.FailWrite = _ => false;
        await Assert.That(await node.Service.SavedAsync()).Succeeded();
    }

    [Test]
    public async Task Saved_WithStorageHanging_AnswersWithinItsTimeout()
    {
        var store = new HangingStore();
        var node = CreateNode(store);
        var service = new RunPersistenceService(
            node.Domain.Versions, node.Domain.Runs, node.Domain.Attempts, node.Domain.Activations, node.Domain.Sequences, node.Domain.BlockedSteps, node.Domain.PendingStarts, node.Domain.EntityVersions, node.Domain.PollTriggers,
            node.Domain.ConfigSnapshotStore, node.Domain.PendingVersions,
            new StorageOptions("test-bucket"), new PersistenceReadiness(), NullLogger<RunPersistenceService>.Instance, store)
        { SaveTimeout = TimeSpan.FromMilliseconds(1) };
        await service.StartingAsync(CancellationToken.None);
        store.Hang = true;
        node.Domain.AddVersion(node.Domain.CreatePipeline().Id);

        await Assert.That(await service.SavedAsync()).Failed();
    }

    [Test]
    public async Task Saved_InEphemeralMode_HasNothingToWaitFor()
    {
        var node = CreateNode(null, StorageMode.Ephemeral);
        await node.Service.StartingAsync(CancellationToken.None);

        await Assert.That(await node.Service.SavedAsync()).Succeeded();
    }

    private sealed class HangingStore : ISnapshotStore
    {
        public bool Hang { get; set; }
        public Task<byte[]?> TryReadAsync(string key, CancellationToken ct) => Task.FromResult<byte[]?>(null);
        public Task WriteAsync(string key, byte[] content, CancellationToken ct) => Hang ? new TaskCompletionSource().Task : Task.CompletedTask;
        public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
    }

    // Catches: a caller released by a flush that read the state before its change.
    [Test]
    public async Task CoalescingFlush_ACallerDuringAFlush_WaitsForTheNextOne_AndManyShareIt()
    {
        var flushes = new List<TaskCompletionSource<bool>>();
        var started = new SemaphoreSlim(0);
        var flush = new CoalescingFlush(() =>
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (flushes)
                flushes.Add(gate);
            started.Release();
            return gate.Task;
        });

        var first = flush.RequestAsync();
        await started.WaitAsync();
        var during = Enumerable.Range(0, 5).Select(_ => flush.RequestAsync()).ToList();

        flushes[0].SetResult(true);
        await Assert.That(await first).IsTrue();
        await Assert.That(during.Any(t => t.IsCompleted)).IsFalse();

        await started.WaitAsync();
        flushes[1].SetResult(false);

        await Assert.That(await Task.WhenAll(during)).IsEquivalentTo([false, false, false, false, false]);
        await Assert.That(flushes.Count).IsEqualTo(2);
    }

    [Test]
    public async Task CoalescingFlush_AThrowingFlush_IsAFailedSave()
    {
        var flush = new CoalescingFlush(() => throw new IOException("storage is gone"));

        await Assert.That(await flush.RequestAsync()).IsFalse();
    }

    // ---- protocol ----

    [Test]
    public async Task LoadThrows_DoesNotSave_AndFailsStartup()
    {
        var store = new FakeObjectStore { ReadException = new InvalidOperationException("STS auth failed") };
        var node = CreateNode(store);

        await Assert.That(async () => await node.Service.StartingAsync(CancellationToken.None)).Throws<InvalidOperationException>();

        await Assert.That(store.Writes).IsEmpty();
        await Assert.That(node.Readiness.IsReady).IsFalse();
    }

    [Test]
    public async Task CorruptIndex_DoesNotSave_AndFailsStartup()
    {
        var store = new FakeObjectStore();
        store.Objects[RunPersistenceService.RunsIndexKey] = "{ not valid json"u8.ToArray();
        var node = CreateNode(store);

        await Assert.That(async () => await node.Service.StartingAsync(CancellationToken.None)).Throws<JsonException>();

        await Assert.That(store.Writes).IsEmpty();
        await Assert.That(node.Readiness.IsReady).IsFalse();
    }

    [Test]
    public async Task IndexListingMissingRunObject_FailsStartup_WithoutSaving()
    {
        var store = new FakeObjectStore();
        var pipelineId = Olve.Utilities.Ids.Id.New<Pipeline>();
        store.Objects[RunPersistenceService.RunsIndexKey] = JsonSerializer.SerializeToUtf8Bytes(
            new RunIndexSnapshot([new RunIndexEntry(Olve.Utilities.Ids.Id.New<Run>(), pipelineId, 7)]), Json.RunIndexSnapshot);
        var node = CreateNode(store);

        await Assert.That(async () => await node.Service.StartingAsync(CancellationToken.None)).Throws<InvalidDataException>();

        await Assert.That(store.Writes).IsEmpty();
        await Assert.That(node.Readiness.IsReady).IsFalse();
    }

    [Test]
    public async Task IndexListingMissingManifest_FailsStartup_WithoutSaving()
    {
        var store = new FakeObjectStore();
        var source = new TestRunDomain();
        var version = source.AddVersion(source.CreatePipeline().Id);
        store.Objects[RunPersistenceService.VersionsIndexKey] = JsonSerializer.SerializeToUtf8Bytes(
            new PipelineVersionIndexSnapshot([PipelineVersionMetadata.From(version)]), Json.PipelineVersionIndexSnapshot);
        var node = CreateNode(store);

        await Assert.That(async () => await node.Service.StartingAsync(CancellationToken.None)).Throws<InvalidDataException>();

        await Assert.That(store.Writes).IsEmpty();
    }

    [Test]
    public async Task FirstRun_WritesEmptyBaselines_AndBecomesReady()
    {
        var store = new FakeObjectStore();
        var node = CreateNode(store);

        await node.Service.StartingAsync(CancellationToken.None);

        await Assert.That(node.Readiness.IsReady).IsTrue();
        await Assert.That(store.Objects.Keys).IsEquivalentTo(new[]
        {
            RunPersistenceService.VersionsIndexKey, RunPersistenceService.StateKey, RunPersistenceService.RunsIndexKey,
            RunPersistenceService.ConfigIndexKey,
        });
    }

    [Test]
    public async Task SuccessfulLoad_NoRedundantSave()
    {
        var store = new FakeObjectStore();
        var first = CreateNode(store);
        await first.Service.StartingAsync(CancellationToken.None);
        var writesAfterBaseline = store.Writes.Count;

        var second = CreateNode(store);
        await second.Service.StartingAsync(CancellationToken.None);

        await Assert.That(store.Writes.Count).IsEqualTo(writesAfterBaseline);
        await Assert.That(second.Readiness.IsReady).IsTrue();
    }

    [Test]
    public async Task EphemeralMode_NoStore_BecomesReadyImmediately()
    {
        var node = CreateNode(store: null, mode: StorageMode.Ephemeral);

        await node.Service.StartingAsync(CancellationToken.None);

        await Assert.That(node.Readiness.IsReady).IsTrue();
    }

    [Test]
    public async Task PersistentMode_WithNoStore_FailsStartup()
    {
        var node = CreateNode(store: null);

        await Assert.That(async () => await node.Service.StartingAsync(CancellationToken.None)).Throws<InvalidOperationException>();

        await Assert.That(node.Readiness.IsReady).IsFalse();
    }

    // ---- round trip ----

    [Test]
    public async Task Restart_RestoresVersionsSwitchRunsAttemptsAndNumbering()
    {
        var store = new FakeObjectStore();
        var a = await StartNode(store);
        var d = a.Domain;

        var pipeline = d.CreatePipeline();
        var v1 = d.AddVersion(pipeline.Id, manifest: """{"apiVersion":"2.0","v":1}""",
            steps: new Dictionary<string, int> { ["build"] = 1, ["deploy"] = 4 });
        var v2 = d.AddVersion(pipeline.Id, manifest: """{"apiVersion":"2.0","v":2}""", hash: "sha256:v2");
        TestRunDomain.Pick(d.ActivationService.BeginSwitch(pipeline.Id, v1.Id));
        TestRunDomain.Pick(d.ActivationService.BeginSwitch(pipeline.Id, v2.Id, cancelRunning: true)); // Draining(1 → 2)

        var run1 = d.CreateRun(v1, input: """{"sha":"aaa"}""", trigger: new RunTrigger(RunTriggerKind.Push, "sha:aaa"));
        var built = d.Schedule("build", run1);
        TestRunDomain.Pick(d.AttemptService.MarkInProgress(built.Id));
        await Assert.That(d.AttemptService.MarkSucceeded(built.Id, """{"image":"app:1"}""")).Succeeded();
        var deploying = d.Schedule("deploy", run1);
        TestRunDomain.Pick(d.AttemptService.MarkInProgress(deploying.Id)); // in flight across the restart
        var skipped = TestRunDomain.Pick(d.AttemptService.RecordSkipped(run1.Id, "deploy-prod", "when: not prod"));
        var run2 = d.CreateRun(v1, rerunOf: run1.Id);
        var run3 = d.CreateRun(v1);
        TestRunDomain.Pick(d.RunService.Supersede(run2.Id, run3.Id));
        await Assert.That(d.RunService.Delete(run3.Id)).Succeeded(); // newest deleted: its number must stay burnt

        await a.Service.StoppingAsync(CancellationToken.None);
        a.Service.Dispose();

        var b = CreateNode(store);
        await b.Service.StartingAsync(CancellationToken.None);
        var e = b.Domain;
        e.Pipelines.Set(pipeline); // restored by ConfigurationPersistenceService in the real app

        await Assert.That(b.Readiness.IsReady).IsTrue();

        // Versions, manifests included.
        await Assert.That(e.VersionService.TryGet(v1.Id, out var v1b)).IsTrue();
        await Assert.That(v1b!.Manifest).IsEqualTo(v1.Manifest);
        await Assert.That(v1b with { StepVersions = v1.StepVersions, ModelVersions = v1.ModelVersions }).IsEqualTo(v1);
        await Assert.That(v1b.StepVersions).IsEquivalentTo(v1.StepVersions);
        await Assert.That(e.VersionService.TryGet(v2.Id, out var v2b)).IsTrue();
        await Assert.That(v2b!.ManifestHash).IsEqualTo("sha256:v2");

        // The in-progress switch survives.
        await Assert.That(e.ActivationService.TryGet(pipeline.Id, out var activation)).IsTrue();
        await Assert.That(activation).IsTypeOf<Draining>();
        var draining = (Draining)activation!;
        await Assert.That((draining.FromVersionId, draining.ToVersionId, draining.CancelRunning)).IsEqualTo((v1.Id, v2.Id, true));

        // Runs and attempts, exactly.
        d.Runs.TryGet(run1.Id, out var run1Before);
        d.Runs.TryGet(run2.Id, out var run2Before);
        await Assert.That(e.Runs.List()).Count().IsEqualTo(2);
        e.Runs.TryGet(run1.Id, out var run1After);
        e.Runs.TryGet(run2.Id, out var run2After);
        await Assert.That(run1After).IsEqualTo(run1Before);
        await Assert.That(run2After).IsEqualTo(run2Before);
        foreach (var id in new[] { built.Id, deploying.Id, skipped.Id })
            await Assert.That(e.Get(id)).IsEqualTo(d.Get(id));
        await Assert.That(e.Get(deploying.Id).Status).IsTypeOf<AttemptStatus.InProgress>();

        // Numbering continues: run 3 was deleted but its number is never reused; versions continue too.
        await Assert.That(e.CreateRun(v2b!).Number).IsEqualTo(4);
        await Assert.That(e.AddVersion(pipeline.Id).Number).IsEqualTo(3);
        await Assert.That(e.Schedule("build", run1After!).Number).IsEqualTo(2);
    }

    [Test]
    public async Task ChangesMadeDuringStartup_BeforeStarted_AreSaved()
    {
        var store = new FakeObjectStore();
        var a = CreateNode(store);
        await a.Service.StartingAsync(CancellationToken.None);
        // What an IRunOnStartup does between StartingAsync and StartedAsync (e.g. draining a queued start).
        var pipeline = a.Domain.CreatePipeline();
        a.Domain.BlockedSteps.Set(pipeline.Id, new PipelineBlockedSteps(["deploy-prod"]));
        await a.Service.StartedAsync(CancellationToken.None);
        await a.Service.StoppingAsync(CancellationToken.None);
        a.Service.Dispose();

        var b = CreateNode(store);
        await b.Service.StartingAsync(CancellationToken.None);

        await Assert.That(b.Domain.BlockedSteps.TryGet(pipeline.Id, out var blocked)).IsTrue();
        await Assert.That(blocked!.BlockedSteps).IsEquivalentTo(["deploy-prod"]);
    }

    [Test]
    public async Task Restart_RestoresBlockedStepsAndQueuedRunStart()
    {
        var store = new FakeObjectStore();
        var a = await StartNode(store);
        var pipeline = a.Domain.CreatePipeline();
        a.Domain.BlockedSteps.Set(pipeline.Id, new PipelineBlockedSteps(["deploy-prod"]));
        var queued = new PendingRunStart("""{"sha":"aaa"}""", new RunTrigger(RunTriggerKind.Push, "sha:aaa"), DateTimeOffset.UnixEpoch);
        a.Domain.PendingStarts.Set(pipeline.Id, queued);
        await a.Service.StoppingAsync(CancellationToken.None);
        a.Service.Dispose();

        var b = CreateNode(store);
        await b.Service.StartingAsync(CancellationToken.None);

        await Assert.That(b.Domain.BlockedSteps.TryGet(pipeline.Id, out var blocked)).IsTrue();
        await Assert.That(blocked!.BlockedSteps).IsEquivalentTo(["deploy-prod"]);
        await Assert.That(b.Domain.PendingStarts.TryGet(pipeline.Id, out var pending)).IsTrue();
        await Assert.That(pending).IsEqualTo(queued);
    }

    // Catches: step and model versions renumbered after a restart, marking every change wrongly.
    [Test]
    public async Task Restart_RestoresEntityVersionHistory()
    {
        var store = new FakeObjectStore();
        var a = await StartNode(store);
        var pipeline = a.Domain.CreatePipeline();
        var history = ManifestVersioning.Compute(null, Manifests.TestFixtureManifest.Parse()).History;
        a.Domain.EntityVersions.Set(pipeline.Id, history);
        await a.Service.StoppingAsync(CancellationToken.None);
        a.Service.Dispose();

        var b = CreateNode(store);
        await b.Service.StartingAsync(CancellationToken.None);

        await Assert.That(b.Domain.EntityVersions.TryGet(pipeline.Id, out var restored)).IsTrue();
        await Assert.That(JsonSerializer.Serialize(restored!, Json.Options)).IsEqualTo(JsonSerializer.Serialize(history, Json.Options));
    }

    // Catches: ready as soon as another gate is, serving empty run state from /api/ready onwards.
    [Test]
    public async Task Readiness_WaitsForThisServiceToo()
    {
        var store = new FakeObjectStore { ReadException = new InvalidOperationException("the bucket is unreachable") };
        var node = CreateNode(store);
        node.Readiness.Register("config");
        node.Readiness.MarkReady("config");

        await Assert.That(async () => await node.Service.StartingAsync(CancellationToken.None)).Throws<InvalidOperationException>();

        await Assert.That(node.Readiness.IsReady).IsFalse();
    }

    [Test]
    public async Task Readiness_NeedsEveryGate()
    {
        var readiness = new PersistenceReadiness();
        readiness.Register("a");
        readiness.Register("b");

        readiness.MarkReady("a");
        await Assert.That(readiness.IsReady).IsFalse();

        readiness.MarkReady("b");
        await Assert.That(readiness.IsReady).IsTrue();
    }

    [Test]
    public async Task Restart_RestoresConfigSnapshotsAsWritten_AndThePendingVersion()
    {
        var store = new FakeObjectStore();
        var a = await StartNode(store);
        var pipeline = a.Domain.CreatePipeline();
        const string yaml = "# kept\ntarget: {host: h}\n";
        TestRunDomain.Pick(a.Domain.ConfigSnapshots.Add(pipeline.Id, yaml, "dev"));
        TestRunDomain.Pick(a.Domain.ConfigSnapshots.Add(pipeline.Id, yaml, "dev", restoredFrom: 1));
        var version = a.Domain.AddVersion(pipeline.Id);
        a.Domain.PendingVersions.Set(pipeline.Id, new PendingVersion(version.Id, version.Number, DateTimeOffset.UnixEpoch,
            ["config config.target: is required"], [new ChangedDefault("target.namespace", "\"a\"", "\"b\"")]));
        await a.Service.StoppingAsync(CancellationToken.None);
        a.Service.Dispose();

        await Assert.That(store.Objects.Keys).Contains(StorageLayout.ConfigSnapshotKey(pipeline.Id, 2));

        var b = CreateNode(store);
        await b.Service.StartingAsync(CancellationToken.None);

        var snapshots = b.Domain.ConfigSnapshots.ListByPipeline(pipeline.Id);
        await Assert.That(snapshots.Select(x => x.Number)).IsEquivalentTo([2, 1]);
        await Assert.That(snapshots[0].Yaml).IsEqualTo(yaml);
        await Assert.That(snapshots[0].RestoredFrom).IsEqualTo(1);
        await Assert.That(b.Domain.PendingVersions.TryGet(pipeline.Id, out var pending)).IsTrue();
        await Assert.That(pending!.VersionNumber).IsEqualTo(version.Number);
        await Assert.That(pending.Reasons).IsEquivalentTo(["config config.target: is required"]);
        await Assert.That(pending.ChangedDefaults.Single().Path).IsEqualTo("target.namespace");
    }

    [Test]
    public async Task Restart_RestoresPollTriggerLastValues()
    {
        var store = new FakeObjectStore();
        var a = await StartNode(store);
        var pipeline = a.Domain.CreatePipeline();
        var states = new PollTriggerStates(new Dictionary<string, PollTriggerState>
        {
            ["poll"] = new("https://example.test/head", "abc123", DateTimeOffset.UnixEpoch),
        });
        a.Domain.PollTriggers.Set(pipeline.Id, states);
        await a.Service.StoppingAsync(CancellationToken.None);
        a.Service.Dispose();

        var b = CreateNode(store);
        await b.Service.StartingAsync(CancellationToken.None);

        await Assert.That(b.Domain.PollTriggers.TryGet(pipeline.Id, out var loaded)).IsTrue();
        await Assert.That(loaded!.Triggers["poll"]).IsEqualTo(states.Triggers["poll"]);
    }

    [Test]
    public async Task PipelineStateWrittenBeforeBlockedStepsExisted_Loads()
    {
        var store = new FakeObjectStore();
        store.Objects[RunPersistenceService.StateKey] = """{"Activations":[],"Sequences":[]}"""u8.ToArray();
        var node = CreateNode(store);

        await node.Service.StartingAsync(CancellationToken.None);

        await Assert.That(node.Readiness.IsReady).IsTrue();
        await Assert.That(node.Domain.BlockedSteps.GetAll()).IsEmpty();
        await Assert.That(node.Domain.PendingStarts.GetAll()).IsEmpty();
    }

    [Test]
    public async Task PipelineStateWithBlockedStepsUnderTheirFormerName_Loads()
    {
        var store = new FakeObjectStore();
        var pipelineId = Guid.NewGuid();
        store.Objects[RunPersistenceService.StateKey] = System.Text.Encoding.UTF8.GetBytes(
            $$$"""{"Activations":[],"Sequences":[],"Gates":[{"PipelineId":"{{{pipelineId}}}","Gates":{"BlockedSteps":["deploy-prod"]}}]}""");
        var node = CreateNode(store);

        await node.Service.StartingAsync(CancellationToken.None);

        await Assert.That(node.Domain.BlockedSteps.GetAll().Single().Value.BlockedSteps).IsEquivalentTo(["deploy-prod"]);
    }

    // ---- incremental writes ----

    private sealed record SavedChange(Action Apply, Func<TestRunDomain, bool> Survived);

    private static void Must(Result result)
    {
        if (result.TryPickProblems(out var problems))
            throw new InvalidOperationException(string.Join("; ", problems.Select(p => p.ToString())));
    }

    private static void Must(DeletionResult result)
    {
        if (result.TryPickProblems(out var problems))
            throw new InvalidOperationException(string.Join("; ", problems.Select(p => p.ToString())));
    }

    private static IEnumerable<KeyValuePair<string, Func<TestRunDomain, SavedChange>>> SetAndRemoved<T>(
        string name, Func<TestRunDomain, AttachmentStore<Pipeline, T>> attachments, T value)
        where T : class
    {
        yield return new($"{name} set", d =>
        {
            var pipeline = d.CreatePipeline();
            return new(() => attachments(d).Set(pipeline.Id, value), e => attachments(e).TryGet(pipeline.Id, out _));
        });
        yield return new($"{name} removed", d =>
        {
            var pipeline = d.CreatePipeline();
            attachments(d).Set(pipeline.Id, value);
            return new(() => attachments(d).Remove(pipeline.Id), e => !attachments(e).TryGet(pipeline.Id, out _));
        });
    }

    private static readonly Dictionary<string, Func<TestRunDomain, SavedChange>> SavedChanges = new(
    [
        .. SetAndRemoved("activation", d => d.Activations, (PipelineActivation)new Active(Id.New<PipelineVersion>(), 1, DateTimeOffset.UnixEpoch)),
        .. SetAndRemoved("sequences", d => d.Sequences, new PipelineSequences(5, 6)),
        .. SetAndRemoved("blocked steps", d => d.BlockedSteps, new PipelineBlockedSteps(["deploy"])),
        .. SetAndRemoved("queued run start", d => d.PendingStarts, new PendingRunStart("{}", new RunTrigger(RunTriggerKind.Manual), DateTimeOffset.UnixEpoch)),
        .. SetAndRemoved("entity versions", d => d.EntityVersions, EntityVersionMap.Empty),
        .. SetAndRemoved("poll trigger values", d => d.PollTriggers, new PollTriggerStates(new Dictionary<string, PollTriggerState>())),
        .. SetAndRemoved("pending version", d => d.PendingVersions, new PendingVersion(Id.New<PipelineVersion>(), 1, DateTimeOffset.UnixEpoch, [], [])),
        new("version added", d =>
        {
            var pipeline = d.CreatePipeline();
            PipelineVersion? added = null;
            return new(() => added = d.AddVersion(pipeline.Id), e => e.Versions.TryGet(added!.Id, out _));
        }),
        new("version updated", d =>
        {
            var version = d.AddVersion(d.CreatePipeline().Id);
            return new(() => Must(d.Versions.Mutate(version.Id, v => v with { CommitSha = "changed" })),
                e => e.Versions.TryGet(version.Id, out var after) && after.CommitSha == "changed");
        }),
        new("version deleted", d =>
        {
            var version = d.AddVersion(d.CreatePipeline().Id);
            return new(() => Must(d.VersionService.Delete(version.Id)), e => !e.Versions.TryGet(version.Id, out _));
        }),
        new("config snapshot added", d =>
        {
            var pipeline = d.CreatePipeline();
            ConfigSnapshot? added = null;
            return new(() => added = TestRunDomain.Pick(d.ConfigSnapshots.Add(pipeline.Id, "a: 1\n", "dev")), e => e.ConfigSnapshotStore.TryGet(added!.Id, out _));
        }),
        new("config snapshot deleted", d =>
        {
            var pipeline = d.CreatePipeline();
            var snapshot = TestRunDomain.Pick(d.ConfigSnapshots.Add(pipeline.Id, "a: 1\n", "dev"));
            return new(() => d.ConfigSnapshots.DeleteAll(pipeline.Id), e => !e.ConfigSnapshotStore.TryGet(snapshot.Id, out _));
        }),
        new("run added", d =>
        {
            var version = d.AddVersion(d.CreatePipeline().Id);
            Run? added = null;
            return new(() => added = d.CreateRun(version), e => e.Runs.TryGet(added!.Id, out _));
        }),
        new("run cancelled with no attempt change", d =>
        {
            var run = d.CreateRun();
            return new(() => TestRunDomain.Pick(d.RunService.Cancel(run.Id)),
                e => e.Runs.TryGet(run.Id, out var after) && after.Status is RunStatus.Cancelled);
        }),
        new("run deleted", d =>
        {
            var run = d.CreateRun();
            return new(() => Must(d.RunService.Delete(run.Id)), e => !e.Runs.TryGet(run.Id, out _));
        }),
        new("attempt added", d =>
        {
            var run = d.CreateRun();
            Attempt? added = null;
            return new(() => added = d.Schedule("build", run), e => e.Attempts.TryGet(added!.Id, out _));
        }),
        new("attempt updated", d =>
        {
            var attempt = d.Schedule();
            return new(() => TestRunDomain.Pick(d.AttemptService.MarkInProgress(attempt.Id)),
                e => e.Attempts.TryGet(attempt.Id, out var after) && after.Status is AttemptStatus.InProgress);
        }),
        new("attempt deleted", d =>
        {
            var attempt = d.Schedule();
            return new(() => Must(d.Attempts.Delete(attempt.Id)), e => !e.Attempts.TryGet(attempt.Id, out _));
        }),
    ]);

    public static IEnumerable<string> SavedChangeNames() => SavedChanges.Keys;

    // Catches: a dropped subscription, which loses that change across a restart. pipeline-state.json holds
    // everything, so a dropped subscription still saves whenever something else dirties it: the change has
    // to be the only one since a quiet flush, and there is no flush timer (only StartedAsync starts it).
    [Test]
    [MethodDataSource(nameof(SavedChangeNames))]
    public async Task EachChange_IsSavedOnItsOwn(string name)
    {
        var store = new FakeObjectStore();
        var a = CreateNode(store);
        await a.Service.StartingAsync(CancellationToken.None);
        var change = SavedChanges[name](a.Domain);
        await Assert.That(await a.Service.FlushAsync(CancellationToken.None)).IsTrue();

        change.Apply();

        await Assert.That(await a.Service.SavedAsync()).Succeeded();
        a.Service.Dispose();

        var b = CreateNode(store);
        await b.Service.StartingAsync(CancellationToken.None);
        await Assert.That(change.Survived(b.Domain)).IsTrue();
    }

    [Test]
    public async Task Flush_RewritesOnlyChangedRuns_AndIndexOnlyOnMembershipChange()
    {
        var store = new FakeObjectStore();
        var node = await StartNode(store);
        var d = node.Domain;
        var version = d.AddVersion(d.CreatePipeline().Id);
        var quiet = d.CreateRun(version);
        var busy = d.CreateRun(version);
        var attempt = d.Schedule("build", busy);
        await node.Service.FlushAsync(CancellationToken.None);

        var before = store.Writes.Count;
        TestRunDomain.Pick(d.AttemptService.MarkInProgress(attempt.Id));
        await node.Service.FlushAsync(CancellationToken.None);
        var written = store.Writes.Skip(before).ToArray();

        await Assert.That(written).IsEquivalentTo(new[] { StorageLayout.RunStateKey(busy.PipelineId, busy.Number) });
        await Assert.That(written).DoesNotContain(StorageLayout.RunStateKey(quiet.PipelineId, quiet.Number));

        var doc = store.Read(StorageLayout.RunStateKey(busy.PipelineId, busy.Number), Json.RunDocument);
        await Assert.That(doc.Attempts.Single().Status).IsTypeOf<AttemptStatus.InProgress>();
        node.Service.Dispose();
    }

    [Test]
    public async Task DeletedRun_LeavesIndex_ThenItsObjectIsDeleted()
    {
        var store = new FakeObjectStore();
        var node = await StartNode(store);
        var d = node.Domain;
        var run = d.CreateRun();
        await node.Service.FlushAsync(CancellationToken.None);
        var key = StorageLayout.RunStateKey(run.PipelineId, run.Number);
        await Assert.That(store.Objects.ContainsKey(key)).IsTrue();

        await Assert.That(d.RunService.Delete(run.Id)).Succeeded();
        await node.Service.FlushAsync(CancellationToken.None);

        await Assert.That(store.Read(RunPersistenceService.RunsIndexKey, Json.RunIndexSnapshot).Runs).IsEmpty();
        await Assert.That(store.Objects.ContainsKey(key)).IsFalse();
        node.Service.Dispose();
    }

    // A write-once manifest that fails to write must be retried, and until it lands the index must not
    // reference it (else the next start would crash on the missing object).
    [Test]
    public async Task FailedManifestWrite_IsExcludedFromIndex_AndRetried()
    {
        var store = new FakeObjectStore();
        var node = await StartNode(store);
        var d = node.Domain;
        var failing = true;
        store.FailWrite = key => failing && key.EndsWith("manifest.json", StringComparison.Ordinal);

        var version = d.AddVersion(d.CreatePipeline().Id);
        await node.Service.FlushAsync(CancellationToken.None);

        await Assert.That(store.Read(RunPersistenceService.VersionsIndexKey, Json.PipelineVersionIndexSnapshot).Versions).IsEmpty();

        failing = false;
        await node.Service.FlushAsync(CancellationToken.None);

        var index = store.Read(RunPersistenceService.VersionsIndexKey, Json.PipelineVersionIndexSnapshot);
        await Assert.That(index.Versions.Single().Id).IsEqualTo(version.Id);
        await Assert.That(Encoding.UTF8.GetString(store.Objects[StorageLayout.VersionManifestKey(version)])).IsEqualTo(version.Manifest);
        node.Service.Dispose();
    }

    [Test]
    public async Task FailedRunWrite_IsRetried_AndThenIndexed()
    {
        var store = new FakeObjectStore();
        var node = await StartNode(store);
        var d = node.Domain;
        var failing = true;
        store.FailWrite = key => failing && key.EndsWith("run.json", StringComparison.Ordinal);

        var run = d.CreateRun();
        await node.Service.FlushAsync(CancellationToken.None);
        await Assert.That(store.Read(RunPersistenceService.RunsIndexKey, Json.RunIndexSnapshot).Runs).IsEmpty();

        failing = false;
        await node.Service.FlushAsync(CancellationToken.None);

        await Assert.That(store.Read(RunPersistenceService.RunsIndexKey, Json.RunIndexSnapshot).Runs.Single().Id).IsEqualTo(run.Id);
        node.Service.Dispose();
    }

    [Test]
    public async Task NothingIsWrittenBeforeLoadConfirmed()
    {
        var store = new FakeObjectStore();
        var node = CreateNode(store);
        // Never started: the write gate must hold even if a flush is requested.
        node.Domain.CreateRun();

        await node.Service.FlushAsync(CancellationToken.None);

        await Assert.That(store.Writes).IsEmpty();
    }
}
