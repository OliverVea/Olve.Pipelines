using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Versions;

namespace Olve.Pipelines.Shared.Persistence;

/// <summary>
/// Persists the v2 run domain — pipeline versions, active-version/switch state, number sequences,
/// runs and attempts — so all of it survives a restart (spec §5.4), notably v2's own self-deploy.
///
/// <para><b>Layout.</b> Small indexes plus per-object documents, instead of one whole-state snapshot:</para>
/// <code>
/// pipeline-versions.json              version metadata (no manifests)
/// versions/&lt;p&gt;/&lt;n&gt;/manifest.json      one manifest per version, written once (immutable)
/// pipeline-state.json                 activations (Active/Draining), sequences, gates, queued run starts
/// runs.json                           which runs exist: (id, pipeline, number)
/// runs/&lt;p&gt;/&lt;n&gt;/run.json              one run + all its attempts (incl. capped outputs)
/// </code>
/// <para><b>Why.</b> Attempts change status constantly while a pipeline runs, and each carries up to
/// <see cref="AttemptService.MaxOutputJsonBytes"/> of output; manifests embed JSON Schemas. A whole-state
/// snapshot would rewrite all retained history (and every manifest) on every status change. Here a
/// flush rewrites only the runs that changed, plus an index only when membership changed. A run and
/// its attempts live in one object, so they are always mutually consistent after a restart. The
/// indexes stay small (one short entry per run/version), and the run objects sit beside the step
/// outputs in <c>runs/&lt;p&gt;/&lt;n&gt;/</c> (see <see cref="StorageLayout"/>).</para>
///
/// <para><b>Protocol</b> (same guarantees as the other snapshot services): readiness gate; a failed or
/// corrupt read fails startup without writing (an index entry whose object is missing counts as
/// corrupt); first run writes empty baselines; writes are gated on a confirmed load; 1 s dirty flush;
/// final flush on stop. Ordering invariant: an index only lists objects already durably written
/// (tracked in <c>_written*</c>), and an object is deleted only after an index without it was written,
/// so a crash at any point leaves a loadable state (at worst an orphaned object).</para>
/// </summary>
public class RunPersistenceService(
    EntityStore<PipelineVersion> versions,
    EntityStore<Run> runs,
    EntityStore<Attempt> attempts,
    AttachmentStore<Pipeline, PipelineActivation> activations,
    AttachmentStore<Pipeline, PipelineSequences> sequences,
    AttachmentStore<Pipeline, PipelineStepGates> gates,
    AttachmentStore<Pipeline, PendingRunStart> pendingStarts,
    StorageOptions storageOptions,
    IPersistenceReadiness readiness,
    ILogger<RunPersistenceService> logger,
    ISnapshotStore? store = null) : IHostedLifecycleService, IDisposable
{
    internal const string VersionsIndexKey = "pipeline-versions.json";
    internal const string StateKey = "pipeline-state.json";
    internal const string RunsIndexKey = "runs.json";
    private const string ReadinessKey = "runs";
    private const int LoadParallelism = 16;

    private readonly EntityStoreIndex<Attempt, Id<Run>> _attemptsByRun = attempts.CreateIndex(a => a.RunId);
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    private readonly ConcurrentDictionary<Id<PipelineVersion>, byte> _pendingManifests = new();
    private readonly ConcurrentDictionary<Id<PipelineVersion>, byte> _writtenManifests = new();
    private readonly ConcurrentDictionary<Id<Run>, byte> _dirtyRuns = new();
    private readonly ConcurrentDictionary<Id<Run>, byte> _writtenRuns = new();
    private readonly ConcurrentQueue<string> _deletedKeys = new();
    private readonly DirtyFlag _versionsIndexDirty = new();
    private readonly DirtyFlag _stateDirty = new();
    private readonly DirtyFlag _runsIndexDirty = new();

    private volatile bool _loaded;
    private Timer? _timer;

    private static RunPersistenceJsonContext Json => RunPersistenceJsonContext.Default;

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        readiness.Register(ReadinessKey);

        if (storageOptions.Mode == StorageMode.Ephemeral)
        {
            logger.LogInformation("Ephemeral storage mode: runs and versions will not be persisted");
            readiness.MarkReady(ReadinessKey);
            return;
        }

        if (store is null)
        {
            throw new InvalidOperationException(
                "Persistent storage mode requires storage configuration (Storage:Endpoint and credentials), " +
                "but none was provided. Set Storage:Mode=Ephemeral for in-memory-only operation.");
        }

        // Throws (without writing anything) on a failed or corrupt read: the pod crashloops until
        // storage recovers or a human restores the state, instead of overwriting it with empty.
        var anyIndexMissing = await LoadAsync(store, cancellationToken);

        _loaded = true;

        if (anyIndexMissing)
        {
            // First run (or a newly introduced file): writing empty baselines is safe.
            _versionsIndexDirty.Set();
            _stateDirty.Set();
            _runsIndexDirty.Set();
            await FlushAsync(cancellationToken);
        }

        readiness.MarkReady(ReadinessKey);
    }

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        versions.OnAdded.Subscribe(e => OnVersionChanged(e.Id));
        versions.OnUpdated.Subscribe(e => OnVersionChanged(e.Id));
        versions.OnDeleted.Subscribe(e => OnVersionDeleted(e.Entity));

        activations.OnSet.Subscribe(_ => MarkDirty(_stateDirty));
        activations.OnRemoved.Subscribe(_ => MarkDirty(_stateDirty));
        sequences.OnSet.Subscribe(_ => MarkDirty(_stateDirty));
        sequences.OnRemoved.Subscribe(_ => MarkDirty(_stateDirty));
        gates.OnSet.Subscribe(_ => MarkDirty(_stateDirty));
        gates.OnRemoved.Subscribe(_ => MarkDirty(_stateDirty));
        pendingStarts.OnSet.Subscribe(_ => MarkDirty(_stateDirty));
        pendingStarts.OnRemoved.Subscribe(_ => MarkDirty(_stateDirty));

        runs.OnAdded.Subscribe(e => OnRunChanged(e.Id));
        runs.OnUpdated.Subscribe(e => OnRunChanged(e.Id));
        runs.OnDeleted.Subscribe(e => OnRunDeleted(e.Entity));

        attempts.OnAdded.Subscribe(e => OnRunChanged(e.Entity.RunId));
        attempts.OnUpdated.Subscribe(e => OnRunChanged(e.After.RunId));
        attempts.OnDeleted.Subscribe(e => OnRunChanged(e.Entity.RunId));

        _timer = new Timer(OnTimerTick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        return Task.CompletedTask;
    }

    public Task StoppingAsync(CancellationToken cancellationToken) => FlushAsync(cancellationToken);

    // ---- change tracking (never before a confirmed load) ----

    private void OnVersionChanged(Id<PipelineVersion> id)
    {
        if (!_loaded) return;
        _pendingManifests[id] = 0;
        MarkDirty(_versionsIndexDirty);
    }

    private void OnVersionDeleted(PipelineVersion version)
    {
        if (!_loaded) return;
        _pendingManifests.TryRemove(version.Id, out _);
        _writtenManifests.TryRemove(version.Id, out _);
        // Dirty BEFORE enqueueing the key: a flush that drains the key must also rewrite the index.
        MarkDirty(_versionsIndexDirty);
        _deletedKeys.Enqueue(StorageLayout.VersionManifestKey(version));
    }

    private void OnRunChanged(Id<Run> runId)
    {
        if (!_loaded) return;
        _dirtyRuns[runId] = 0;
    }

    private void OnRunDeleted(Run run)
    {
        if (!_loaded) return;
        _writtenRuns.TryRemove(run.Id, out _);
        MarkDirty(_runsIndexDirty);
        _deletedKeys.Enqueue(StorageLayout.RunStateKey(run.PipelineId, run.Number));
    }

    private void MarkDirty(DirtyFlag flag)
    {
        if (!_loaded) return;
        flag.Set();
    }

    private bool HasWork =>
        !_pendingManifests.IsEmpty || !_dirtyRuns.IsEmpty || !_deletedKeys.IsEmpty
        || _versionsIndexDirty.IsSet || _stateDirty.IsSet || _runsIndexDirty.IsSet;

    private async void OnTimerTick(object? state)
    {
        if (!HasWork) return;

        try
        {
            await FlushAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception while flushing run state");
        }
    }

    // ---- flush ----

    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        // Write-gate: never persist before a load has been confirmed (or with no store at all).
        if (store is null || !_loaded)
            return;

        await _flushGate.WaitAsync(cancellationToken);
        try
        {
            // Keys deleted before this flush: removed only once the indexes no longer list them.
            var toDelete = new List<string>();
            while (_deletedKeys.TryDequeue(out var key))
                toDelete.Add(key);

            var manifests = await WriteManifestsAsync(store, cancellationToken);
            var versionsOk = await WriteIndexIfDirtyAsync(store, VersionsIndexKey, _versionsIndexDirty,
                BuildVersionsIndex, Json.PipelineVersionIndexSnapshot, cancellationToken);
            var stateOk = await WriteIndexIfDirtyAsync(store, StateKey, _stateDirty,
                BuildState, Json.PipelineStateSnapshot, cancellationToken);
            var runDocs = await WriteRunDocumentsAsync(store, cancellationToken);
            var runsOk = await WriteIndexIfDirtyAsync(store, RunsIndexKey, _runsIndexDirty,
                BuildRunsIndex, Json.RunIndexSnapshot, cancellationToken);

            if (versionsOk && runsOk)
            {
                foreach (var key in toDelete)
                    await DeleteBestEffortAsync(store, key, cancellationToken);
            }
            else
            {
                foreach (var key in toDelete)
                    _deletedKeys.Enqueue(key);
            }

            if (manifests + runDocs > 0 || toDelete.Count > 0)
                logger.LogDebug(
                    "Flushed run state: {Manifests} manifest(s), {RunDocuments} run document(s), {Deleted} deletion(s); state ok={StateOk}",
                    manifests, runDocs, versionsOk && runsOk ? toDelete.Count : 0, stateOk);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private async Task<int> WriteManifestsAsync(ISnapshotStore snapshotStore, CancellationToken ct)
    {
        var written = 0;
        foreach (var id in _pendingManifests.Keys)
        {
            _pendingManifests.TryRemove(id, out _);
            if (!versions.TryGet(id, out var version))
                continue;

            try
            {
                await snapshotStore.WriteAsync(StorageLayout.VersionManifestKey(version), Encoding.UTF8.GetBytes(version.Manifest), ct);
                written++;
                if (_writtenManifests.TryAdd(id, 0))
                    _versionsIndexDirty.Set();
            }
            catch (Exception ex)
            {
                // Never lose a write-once object: retry next tick. The index excludes it until then.
                _pendingManifests.TryAdd(id, 0);
                logger.LogError(ex, "Failed to write manifest of version {VersionNumber} of pipeline '{PipelineId}'; will retry",
                    version.Number, version.PipelineId);
            }
        }

        return written;
    }

    private async Task<int> WriteRunDocumentsAsync(ISnapshotStore snapshotStore, CancellationToken ct)
    {
        var written = 0;
        foreach (var runId in _dirtyRuns.Keys)
        {
            _dirtyRuns.TryRemove(runId, out _);
            if (!runs.TryGet(runId, out var run))
                continue; // deleted; its object is removed via _deletedKeys

            var document = new RunDocument(run, AttemptsOf(runId));
            try
            {
                await snapshotStore.WriteAsync(
                    StorageLayout.RunStateKey(run.PipelineId, run.Number),
                    JsonSerializer.SerializeToUtf8Bytes(document, Json.RunDocument),
                    ct);
                written++;
                if (_writtenRuns.TryAdd(runId, 0))
                    _runsIndexDirty.Set();
            }
            catch (Exception ex)
            {
                _dirtyRuns.TryAdd(runId, 0);
                logger.LogError(ex, "Failed to write state of run #{RunNumber} of pipeline '{PipelineId}'; will retry",
                    run.Number, run.PipelineId);
            }
        }

        return written;
    }

    private async Task<bool> WriteIndexIfDirtyAsync<T>(
        ISnapshotStore snapshotStore, string key, DirtyFlag dirty, Func<T> build, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        // Clear before building: a change racing with the write re-sets the flag for the next tick.
        if (!dirty.TryClear())
            return true;

        var ok = await WriteAsync(snapshotStore, key, build(), typeInfo, ct);
        if (!ok)
            dirty.Set();
        return ok;
    }

    private async Task<bool> WriteAsync<T>(ISnapshotStore snapshotStore, string key, T value, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        try
        {
            await snapshotStore.WriteAsync(key, JsonSerializer.SerializeToUtf8Bytes(value, typeInfo), ct);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write '{Key}'; will retry", key);
            return false;
        }
    }

    private async Task DeleteBestEffortAsync(ISnapshotStore snapshotStore, string key, CancellationToken ct)
    {
        try
        {
            await snapshotStore.DeleteAsync(key, ct);
        }
        catch (Exception ex)
        {
            // An orphaned object is harmless: nothing lists it.
            logger.LogWarning(ex, "Failed to delete orphaned object '{Key}'", key);
        }
    }

    private Attempt[] AttemptsOf(Id<Run> runId)
    {
        var result = new List<Attempt>();
        foreach (var id in _attemptsByRun.GetForKey(runId))
        {
            if (attempts.TryGet(id, out var attempt))
                result.Add(attempt);
        }

        return result.OrderBy(a => a.StepName, StringComparer.Ordinal).ThenBy(a => a.Number).ToArray();
    }

    private PipelineVersionIndexSnapshot BuildVersionsIndex() =>
        new(versions.List()
            .Where(v => _writtenManifests.ContainsKey(v.Id))
            .OrderBy(v => v.PipelineId).ThenBy(v => v.Number)
            .Select(PipelineVersionMetadata.From)
            .ToArray());

    private PipelineStateSnapshot BuildState() =>
        new(activations.GetAll().Select(kv => new PipelineActivationEntry(kv.Key, kv.Value)).ToArray(),
            sequences.GetAll().Select(kv => new PipelineSequencesEntry(kv.Key, kv.Value)).ToArray(),
            gates.GetAll().Select(kv => new PipelineStepGatesEntry(kv.Key, kv.Value)).ToArray(),
            pendingStarts.GetAll().Select(kv => new PendingRunStartEntry(kv.Key, kv.Value)).ToArray());

    private RunIndexSnapshot BuildRunsIndex() =>
        new(runs.List()
            .Where(r => _writtenRuns.ContainsKey(r.Id))
            .OrderBy(r => r.PipelineId).ThenBy(r => r.Number)
            .Select(r => new RunIndexEntry(r.Id, r.PipelineId, r.Number))
            .ToArray());

    // ---- load ----

    /// <returns>True when at least one index file did not exist yet.</returns>
    private async Task<bool> LoadAsync(ISnapshotStore snapshotStore, CancellationToken ct)
    {
        var versionsIndex = await ReadAsync(snapshotStore, VersionsIndexKey, Json.PipelineVersionIndexSnapshot, ct);
        var state = await ReadAsync(snapshotStore, StateKey, Json.PipelineStateSnapshot, ct);
        var runsIndex = await ReadAsync(snapshotStore, RunsIndexKey, Json.RunIndexSnapshot, ct);

        var versionEntries = versionsIndex?.Versions ?? [];
        var manifests = new string[versionEntries.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, versionEntries.Length),
            new ParallelOptions { MaxDegreeOfParallelism = LoadParallelism, CancellationToken = ct },
            async (i, token) =>
            {
                var meta = versionEntries[i];
                var key = StorageLayout.VersionManifestKey(meta.PipelineId, meta.Number);
                var bytes = await ReadRawAsync(snapshotStore, key, token)
                            ?? throw Missing(key, $"version {meta.Number} of pipeline '{meta.PipelineId}'");
                manifests[i] = Encoding.UTF8.GetString(bytes);
            });

        var runEntries = runsIndex?.Runs ?? [];
        var documents = new RunDocument[runEntries.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, runEntries.Length),
            new ParallelOptions { MaxDegreeOfParallelism = LoadParallelism, CancellationToken = ct },
            async (i, token) =>
            {
                var entry = runEntries[i];
                var key = StorageLayout.RunStateKey(entry.PipelineId, entry.Number);
                var document = await ReadAsync(snapshotStore, key, Json.RunDocument, token)
                               ?? throw Missing(key, $"run #{entry.Number} of pipeline '{entry.PipelineId}'");
                if (document.Run.Id != entry.Id)
                    throw Corrupt(key, $"holds run '{document.Run.Id}' but the index expects '{entry.Id}'");
                documents[i] = document;
            });

        // Event hubs are not wired yet (StartupRunner runs after every StartingAsync), so these
        // writes reach no domain subscriber; our own change tracking is gated on _loaded.
        for (var i = 0; i < versionEntries.Length; i++)
        {
            versions.Set(versionEntries[i].WithManifest(manifests[i]));
            _writtenManifests[versionEntries[i].Id] = 0;
        }

        foreach (var entry in state?.Activations ?? [])
            activations.Set(entry.PipelineId, entry.Activation);
        foreach (var entry in state?.Sequences ?? [])
            sequences.Set(entry.PipelineId, entry.Sequences);
        foreach (var entry in state?.Gates ?? [])
            gates.Set(entry.PipelineId, entry.Gates);
        foreach (var entry in state?.PendingStarts ?? [])
            pendingStarts.Set(entry.PipelineId, entry.Pending);

        var attemptCount = 0;
        foreach (var document in documents)
        {
            runs.Set(document.Run);
            _writtenRuns[document.Run.Id] = 0;
            foreach (var attempt in document.Attempts)
            {
                // Scheduled/InProgress attempts are reconciled by the next JobRunner tick: the executor
                // derives the K8s Job name from the attempt id and reattaches or submits.
                attempts.Set(attempt);
                attemptCount++;
            }
        }

        logger.LogInformation(
            "Loaded run state: {Versions} versions, {Activations} activations, {Runs} runs, {Attempts} attempts",
            versionEntries.Length, state?.Activations.Length ?? 0, documents.Length, attemptCount);

        return versionsIndex is null || state is null || runsIndex is null;
    }

    private async Task<T?> ReadAsync<T>(ISnapshotStore snapshotStore, string key, JsonTypeInfo<T> typeInfo, CancellationToken ct)
        where T : class
    {
        var data = await ReadRawAsync(snapshotStore, key, ct);
        if (data is null)
            return null;

        try
        {
            return JsonSerializer.Deserialize(data, typeInfo);
        }
        catch (JsonException ex)
        {
            // Corrupt object: terminal. Do NOT save. Fail loudly until a human restores it.
            logger.LogCritical(ex, "'{Key}' in storage is corrupt; failing startup (manual restore required)", key);
            throw;
        }
    }

    private async Task<byte[]?> ReadRawAsync(ISnapshotStore snapshotStore, string key, CancellationToken ct)
    {
        try
        {
            return await snapshotStore.TryReadAsync(key, ct);
        }
        catch (Exception ex)
        {
            // Transient/auth failure. Do NOT save — fail startup so the pod crashloops and retries.
            logger.LogError(ex, "Failed to read '{Key}' from storage; failing startup to avoid overwriting good state", key);
            throw;
        }
    }

    private InvalidDataException Missing(string key, string what)
    {
        logger.LogCritical("'{Key}' ({What}) is listed in an index but missing from storage; failing startup (manual restore required)",
            key, what);
        return new InvalidDataException($"'{key}' ({what}) is listed in an index but missing from storage.");
    }

    private InvalidDataException Corrupt(string key, string detail)
    {
        logger.LogCritical("'{Key}' is inconsistent ({Detail}); failing startup (manual restore required)", key, detail);
        return new InvalidDataException($"'{key}' {detail}.");
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private sealed class DirtyFlag
    {
        private int _value;
        public bool IsSet => Volatile.Read(ref _value) == 1;
        public void Set() => Volatile.Write(ref _value, 1);
        public bool TryClear() => Interlocked.Exchange(ref _value, 0) == 1;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _attemptsByRun.Dispose();
        _flushGate.Dispose();
    }
}
