using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Shared;
using Olve.Pipelines.Shared.Persistence;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests;

public class ConfigurationPersistenceServiceTests
{
    private sealed class FakeSnapshotStore : ISnapshotStore
    {
        public byte[]? Data;
        public Exception? ReadException;
        public int WriteCount;
        public byte[]? LastWritten;

        public Task<byte[]?> TryReadAsync(string key, CancellationToken ct)
        {
            if (ReadException is not null) return Task.FromException<byte[]?>(ReadException);
            return Task.FromResult(Data);
        }

        public Task WriteAsync(string key, byte[] content, CancellationToken ct)
        {
            WriteCount++;
            LastWritten = content;
            Data = content;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
    }

    private static (ConfigurationPersistenceService Service, EntityStore<Pipeline> Pipelines, PersistenceReadiness Readiness)
        CreateService(ISnapshotStore? store, StorageMode mode = StorageMode.Persistent)
    {
        var pipelines = new EntityStore<Pipeline>([]);
        var bindings = new EntityStore<PipelineConfigBinding>([]);
        var readiness = new PersistenceReadiness();

        var service = new ConfigurationPersistenceService(
            pipelines, bindings,
            new StorageOptions("test-bucket", Mode: mode),
            readiness,
            NullLogger<ConfigurationPersistenceService>.Instance,
            store);

        return (service, pipelines, readiness);
    }

    private static byte[] SerializeSnapshot(ConfigurationSnapshot snapshot) =>
        JsonSerializer.SerializeToUtf8Bytes(snapshot, ConfigurationPersistenceJsonContext.Default.ConfigurationSnapshot);

    // The regression: a non-404 load failure must NOT trigger a save (which would overwrite good state).
    [Test]
    public async Task LoadThrows_DoesNotSave_AndFailsStartup()
    {
        var store = new FakeSnapshotStore { ReadException = new InvalidOperationException("STS auth failed") };
        var (service, _, readiness) = CreateService(store);

        await Assert.That(async () => await service.StartingAsync(CancellationToken.None))
            .Throws<InvalidOperationException>();

        await Assert.That(store.WriteCount).IsEqualTo(0);
        await Assert.That(readiness.IsReady).IsFalse();
    }

    [Test]
    public async Task CorruptSnapshot_DoesNotSave_AndFailsStartup()
    {
        var store = new FakeSnapshotStore { Data = "{ this is not valid json"u8.ToArray() };
        var (service, _, readiness) = CreateService(store);

        await Assert.That(async () => await service.StartingAsync(CancellationToken.None))
            .Throws<JsonException>();

        await Assert.That(store.WriteCount).IsEqualTo(0);
        await Assert.That(readiness.IsReady).IsFalse();
    }

    [Test]
    public async Task FirstRun_SavesEmptyBaseline_AndBecomesReady()
    {
        var store = new FakeSnapshotStore { Data = null };
        var (service, _, readiness) = CreateService(store);

        await service.StartingAsync(CancellationToken.None);

        await Assert.That(store.WriteCount).IsEqualTo(1);
        await Assert.That(readiness.IsReady).IsTrue();
    }

    [Test]
    public async Task SuccessfulLoad_PopulatesStore_NoRedundantSave_AndBecomesReady()
    {
        var pipelineId = Id.New<Pipeline>();
        var snapshot = new ConfigurationSnapshot(
            Pipelines: [new PipelineData(pipelineId, "loaded-pipeline")]);
        var store = new FakeSnapshotStore { Data = SerializeSnapshot(snapshot) };
        var (service, pipelines, readiness) = CreateService(store);

        await Assert.That(readiness.IsReady).IsFalse();

        await service.StartingAsync(CancellationToken.None);

        await Assert.That(pipelines.TryGet(pipelineId, out var loaded)).IsTrue();
        await Assert.That(loaded!.Name).IsEqualTo("loaded-pipeline");
        await Assert.That(store.WriteCount).IsEqualTo(0);
        await Assert.That(readiness.IsReady).IsTrue();
    }

    [Test]
    public async Task EphemeralMode_NoStore_BecomesReadyImmediately_AndNeverWrites()
    {
        var (service, _, readiness) = CreateService(store: null, mode: StorageMode.Ephemeral);

        await service.StartingAsync(CancellationToken.None);
        await service.StoppingAsync(CancellationToken.None);

        await Assert.That(readiness.IsReady).IsTrue();
    }

    [Test]
    public async Task PersistentMode_WithNoStore_FailsStartup()
    {
        var (service, _, readiness) = CreateService(store: null, mode: StorageMode.Persistent);

        await Assert.That(async () => await service.StartingAsync(CancellationToken.None))
            .Throws<InvalidOperationException>();

        await Assert.That(readiness.IsReady).IsFalse();
    }

    // Write-gate belt-and-suspenders: a stop/save before load is confirmed must not write.
    [Test]
    public async Task SaveBeforeLoad_WritesNothing()
    {
        var store = new FakeSnapshotStore { ReadException = new InvalidOperationException("boom") };
        var (service, _, _) = CreateService(store);

        await Assert.That(async () => await service.StartingAsync(CancellationToken.None))
            .Throws<InvalidOperationException>();

        // StoppingAsync flushes via SaveAsync — must be gated off because load never confirmed.
        await service.StoppingAsync(CancellationToken.None);

        await Assert.That(store.WriteCount).IsEqualTo(0);
    }

    // Reframed from the old AppFixture integration test NullArraysInSnapshot_ToleratedOnStartup:
    // a snapshot whose collections serialized as JSON null must load cleanly (LoadSnapshot uses
    // `?? []`), not crash startup. In-process so it runs in the code-test pipeline step.
    [Test]
    public async Task NullArraysInSnapshot_ToleratedOnStartup()
    {
        var store = new FakeSnapshotStore
        {
            Data = """{"Pipelines": null, "Bindings": null}"""u8.ToArray(),
        };
        var (service, pipelines, readiness) = CreateService(store);

        await service.StartingAsync(CancellationToken.None);

        await Assert.That(readiness.IsReady).IsTrue();
        await Assert.That(pipelines.List()).IsEmpty();
    }

    // v2 drops the v1 step/trigger collections from the snapshot. A v1-era configuration.json must
    // still load (unknown members skipped) so pipelines and bindings survive the upgrade.
    [Test]
    public async Task V1Snapshot_WithStepsAndTriggers_LoadsPipelines()
    {
        var pipelineId = Id.New<Pipeline>();
        var json = $$"""
            {"Pipelines": [{"Id": "{{pipelineId.Value.Value}}", "Name": "v1-pipeline"}],
             "ProductionSteps": [{"Id": "{{Guid.NewGuid()}}", "Name": "build", "PipelineId": "{{pipelineId.Value.Value}}", "Configuration": null}],
             "ProcessingSteps": [],
             "Triggers": [],
             "Bindings": []}
            """;
        var store = new FakeSnapshotStore { Data = System.Text.Encoding.UTF8.GetBytes(json) };
        var (service, pipelines, readiness) = CreateService(store);

        await service.StartingAsync(CancellationToken.None);

        await Assert.That(readiness.IsReady).IsTrue();
        await Assert.That(pipelines.TryGet(pipelineId, out var loaded)).IsTrue();
        await Assert.That(loaded!.Name).IsEqualTo("v1-pipeline");
    }

    [Test]
    public async Task SnapshotWithPreRenameBindingShas_LoadsThemAsPushAndReconciledSha()
    {
        var pipelineId = Id.New<Pipeline>();
        var bindingId = Id.New<PipelineConfigBinding>();
        string push = new('a', 40), reconciled = new('b', 40);
        var json = $$"""
            {"Pipelines": [{"Id": "{{pipelineId.Value.Value}}", "Name": "p"}],
             "Bindings": [{"Id": "{{bindingId.Value.Value}}", "PipelineId": "{{pipelineId.Value.Value}}", "Repo": "o/r",
               "Branch": "main", "Path": ".pipelines", "CredentialsSecret": null,
               "LastDeployedSha": "{{push}}", "LastSyncedSha": "{{reconciled}}",
               "Status": {"Result": 1, "LastSyncTime": null, "Problems": [], "DeclaredSecrets": [{"Name": "X", "Description": null}]},
               "CreatedAt": "2026-10-01T00:00:00+00:00", "DeployTrigger": 0}]}
            """;
        var store = new FakeSnapshotStore { Data = System.Text.Encoding.UTF8.GetBytes(json) };
        var bindings = new EntityStore<PipelineConfigBinding>([]);
        var service = new ConfigurationPersistenceService(
            new EntityStore<Pipeline>([]), bindings, new StorageOptions("test-bucket", Mode: StorageMode.Persistent),
            new PersistenceReadiness(), NullLogger<ConfigurationPersistenceService>.Instance, store);

        await service.StartingAsync(CancellationToken.None);

        await Assert.That(bindings.TryGet(bindingId, out var binding)).IsTrue();
        await Assert.That(binding!.LastPushSha).IsEqualTo(push);
        await Assert.That(binding.LastReconciledSha).IsEqualTo(reconciled);
        await Assert.That(binding.Status.Result).IsEqualTo(ReconcileResult.Success);

        await service.StoppingAsync(CancellationToken.None);
        var written = System.Text.Encoding.UTF8.GetString(store.LastWritten!);
        await Assert.That(written).Contains($"\"LastPushSha\":\"{push}\"");
        await Assert.That(written).DoesNotContain("LastDeployedSha");
    }

    // Reframed from the old AppFixture integration test CreatedPipeline_SurvivesRestart: state
    // written by one instance is reloaded by a fresh instance over the same store — the "survives
    // restart" guarantee, expressed in-process (a new service == a restarted process).
    [Test]
    public async Task WrittenState_SurvivesAcrossServiceInstances()
    {
        var store = new FakeSnapshotStore();
        var pipelineId = Id.New<Pipeline>();

        // First instance: seed a pipeline and flush on stop.
        var (writer, writerPipelines, _) = CreateService(store);
        await writer.StartingAsync(CancellationToken.None);
        writerPipelines.Set(new Pipeline(pipelineId, "survivor"));
        await writer.StoppingAsync(CancellationToken.None);

        await Assert.That(store.WriteCount).IsGreaterThan(0);

        // Second instance ("after restart"): same store, must reload the seeded pipeline.
        var (reader, readerPipelines, readiness) = CreateService(store);
        await reader.StartingAsync(CancellationToken.None);

        await Assert.That(readiness.IsReady).IsTrue();
        await Assert.That(readerPipelines.TryGet(pipelineId, out var reloaded)).IsTrue();
        await Assert.That(reloaded!.Name).IsEqualTo("survivor");
    }
}
