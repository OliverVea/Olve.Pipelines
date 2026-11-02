using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Shared.Persistence;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests;

public class BindingHookPersistenceServiceTests
{
    private const string Key = "binding-hooks.json";

    private sealed class CountingStore : ISnapshotStore
    {
        public byte[]? Data;
        public Exception? ReadException;
        public int ReadCount;
        public int WriteCount;
        public int FailWrites;
        public string? WrittenKey;

        public Task<byte[]?> TryReadAsync(string key, CancellationToken ct)
        {
            ReadCount++;
            return ReadException is null ? Task.FromResult(key == Key ? Data : null) : Task.FromException<byte[]?>(ReadException);
        }

        public Task WriteAsync(string key, byte[] content, CancellationToken ct)
        {
            WriteCount++;
            if (FailWrites > 0)
            {
                FailWrites--;
                return Task.FromException(new IOException("simulated storage outage"));
            }

            WrittenKey = key;
            Data = content;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
    }

    private static (BindingHookPersistenceService Service, BindingHookStateStore Hooks, PersistenceReadiness Readiness)
        Create(ISnapshotStore? store, StorageMode mode = StorageMode.Persistent)
    {
        var hooks = new BindingHookStateStore();
        var readiness = new PersistenceReadiness();
        var service = new BindingHookPersistenceService(
            hooks, new StorageOptions("test-bucket", Mode: mode), readiness,
            NullLogger<BindingHookPersistenceService>.Instance, store);
        return (service, hooks, readiness);
    }

    private static BindingHookState Hook(long id = 42, string? url = "https://hooks.example/42") =>
        new(Id.New<Pipeline>(), "OliverVea", "Olve.Pipelines", id, "github-pat", url);

    private static BindingHookEntry[] Saved(CountingStore store) =>
        JsonSerializer.Deserialize(store.Data!, BindingHookPersistenceJsonContext.Default.BindingHookSnapshot)!.Hooks;

    // Saving after a failed load would overwrite the hook ids that storage still holds.
    [Test]
    [Arguments("read fails")]
    [Arguments("snapshot corrupt")]
    public async Task UnreadableState_FailsStartup_WithoutSaving(string failure)
    {
        var store = new CountingStore();
        if (failure == "read fails") store.ReadException = new InvalidOperationException("STS auth failed");
        else store.Data = "{ not json"u8.ToArray();
        var (service, _, readiness) = Create(store);
        readiness.Register("other");
        readiness.MarkReady("other");

        Exception? thrown = null;
        try { await service.StartingAsync(CancellationToken.None); }
        catch (Exception ex) { thrown = ex; }
        await service.StoppingAsync(CancellationToken.None);

        await Assert.That(thrown).IsNotNull();
        await Assert.That(thrown is JsonException).IsEqualTo(failure == "snapshot corrupt");
        await Assert.That(store.WriteCount).IsEqualTo(0);
        await Assert.That(readiness.IsReady).IsFalse();
    }

    [Test]
    [Arguments(null)]
    [Arguments("null")]
    public async Task NothingStored_SavesAnEmptyBaseline_AndBecomesReady(string? stored)
    {
        var store = new CountingStore { Data = stored is null ? null : Encoding.UTF8.GetBytes(stored) };
        var (service, _, readiness) = Create(store);

        await service.StartingAsync(CancellationToken.None);

        await Assert.That(store.WriteCount).IsEqualTo(1);
        await Assert.That(store.WrittenKey).IsEqualTo(Key);
        await Assert.That(Saved(store)).IsEmpty();
        await Assert.That(readiness.IsReady).IsTrue();
    }

    [Test]
    public async Task NullHooksInSnapshot_LoadAsNone()
    {
        var store = new CountingStore { Data = """{"Hooks": null}"""u8.ToArray() };
        var (service, hooks, readiness) = Create(store);

        await service.StartingAsync(CancellationToken.None);

        await Assert.That(hooks.GetAll()).IsEmpty();
        await Assert.That(readiness.IsReady).IsTrue();
    }

    // Catches: a load that writes back, or a loaded hook that counts as an unsaved change.
    [Test]
    public async Task LoadedHooks_AreNotSavedAgain()
    {
        var store = new CountingStore();
        var bindingId = Id.New<PipelineConfigBinding>();
        var (writer, writerHooks, _) = Create(store);
        await writer.StartingAsync(CancellationToken.None);
        writerHooks.Set(bindingId, Hook());
        await writer.SaveIfDirtyAsync();
        var writes = store.WriteCount;

        var (reader, _, readiness) = Create(store);
        await reader.StartingAsync(CancellationToken.None);
        await reader.SaveIfDirtyAsync();

        await Assert.That(store.WriteCount).IsEqualTo(writes);
        await Assert.That(readiness.IsReady).IsTrue();
    }

    // A lost hook id leaves a hook on GitHub that a binding switched to poll can no longer remove.
    [Test]
    [Arguments("https://hooks.example/42")]
    [Arguments(null)]
    public async Task EveryHookField_SurvivesARestart(string? url)
    {
        var store = new CountingStore();
        var bindingId = Id.New<PipelineConfigBinding>();
        var otherId = Id.New<PipelineConfigBinding>();
        var hook = Hook(url: url);
        var other = Hook(id: 43);
        var (writer, writerHooks, _) = Create(store);
        await writer.StartingAsync(CancellationToken.None);
        writerHooks.Set(bindingId, hook);
        writerHooks.Set(otherId, other);
        await writer.StoppingAsync(CancellationToken.None);

        var (reader, readerHooks, _) = Create(store);
        await reader.StartingAsync(CancellationToken.None);

        await Assert.That(readerHooks.TryGet(bindingId, out var loaded)).IsTrue();
        await Assert.That(loaded).IsEqualTo(hook);
        await Assert.That(readerHooks.TryGet(otherId, out var loadedOther)).IsTrue();
        await Assert.That(loadedOther).IsEqualTo(other);
    }

    [Test]
    [Arguments("hook added", 2)]
    [Arguments("hook replaced", 1)]
    [Arguments("hook removed", 0)]
    public async Task EveryHookWrite_IsSaved(string write, int expectedHooks)
    {
        var store = new CountingStore();
        var (service, hooks, _) = Create(store);
        await service.StartingAsync(CancellationToken.None);
        var bindingId = Id.New<PipelineConfigBinding>();
        hooks.Set(bindingId, Hook());
        await service.SaveIfDirtyAsync();

        switch (write)
        {
            case "hook added": hooks.Set(Id.New<PipelineConfigBinding>(), Hook(id: 43)); break;
            case "hook replaced": hooks.Set(bindingId, Hook(id: 43)); break;
            case "hook removed": hooks.Remove(bindingId); break;
        }
        await service.SaveIfDirtyAsync();

        var saved = Saved(store);
        await Assert.That(saved.Length).IsEqualTo(expectedHooks);
        await Assert.That(saved.All(h => h.HookId == 42)).IsEqualTo(write == "hook removed");
    }

    [Test]
    public async Task NoChange_WritesNothingOnTheTick()
    {
        var store = new CountingStore();
        var (service, hooks, _) = Create(store);
        await service.StartingAsync(CancellationToken.None);
        hooks.Set(Id.New<PipelineConfigBinding>(), Hook());
        await service.SaveIfDirtyAsync();
        var writes = store.WriteCount;

        await service.SaveIfDirtyAsync();

        await Assert.That(store.WriteCount).IsEqualTo(writes);
    }

    // Catches: a hook id lost because its save failed and nothing marked it unsaved again.
    [Test]
    public async Task FailedSave_IsRetriedOnTheNextTick()
    {
        var store = new CountingStore();
        var (service, hooks, _) = Create(store);
        await service.StartingAsync(CancellationToken.None);
        store.FailWrites = 1;

        hooks.Set(Id.New<PipelineConfigBinding>(), Hook());
        await service.SaveIfDirtyAsync();
        await Assert.That(Saved(store)).IsEmpty();
        await service.SaveIfDirtyAsync();

        await Assert.That(Saved(store).Single().HookId).IsEqualTo(42);
    }

    [Test]
    public async Task EphemeralMode_NeitherLoadsNorSaves_AndBecomesReady()
    {
        var store = new CountingStore { Data = """{"Hooks": []}"""u8.ToArray() };
        var (service, hooks, readiness) = Create(store, StorageMode.Ephemeral);

        await service.StartingAsync(CancellationToken.None);
        hooks.Set(Id.New<PipelineConfigBinding>(), Hook());
        await service.SaveIfDirtyAsync();
        await service.StoppingAsync(CancellationToken.None);

        await Assert.That(readiness.IsReady).IsTrue();
        await Assert.That(store.ReadCount).IsEqualTo(0);
        await Assert.That(store.WriteCount).IsEqualTo(0);
    }

    [Test]
    public async Task PersistentMode_WithNoStore_FailsStartup_NamingTheSettings()
    {
        var (service, _, readiness) = Create(store: null);

        await Assert.That(async () => await service.StartingAsync(CancellationToken.None))
            .Throws<InvalidOperationException>()
            .WithMessage("Persistent storage mode requires storage configuration (Storage:Endpoint and credentials), " +
                         "but none was provided. Set Storage:Mode=Ephemeral for in-memory-only operation.");

        await Assert.That(readiness.IsReady).IsFalse();
    }
}
