using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Shared.Persistence;
using Olve.Results.TUnit;
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
            domain.Versions, domain.Runs, domain.Attempts, domain.Activations, domain.Sequences,
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
        await Assert.That(d.RunService.Delete(run3.Id).Succeeded).IsTrue(); // newest deleted: its number must stay burnt

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

    // ---- incremental writes ----

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

        await Assert.That(d.RunService.Delete(run.Id).Succeeded).IsTrue();
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
