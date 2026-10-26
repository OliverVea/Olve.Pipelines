using System.Collections.Concurrent;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Shared.Storage;

namespace Olve.Pipelines.UnitTests;

internal static class HarnessPodStates
{
    public static readonly HarnessContainerState InitOk = new(HarnessJobBuilder.InitContainerName, 0, "Completed", null);
    public static readonly HarnessPodState Running = new(null, null, InitOk, new HarnessContainerState("step", null, null, null));
    public static readonly HarnessPodState Exited0 = new(null, null, InitOk, new HarnessContainerState("step", 0, "Completed", null));
}

internal sealed class FakeKubernetes : IKubernetesClient, IHarnessKubernetesClient
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
    public Action? OnCreateJob { get; set; }
    public System.Threading.Channels.Channel<string> JobChanges { get; } = System.Threading.Channels.Channel.CreateUnbounded<string>();
    /// <summary>When set, a pod watch is refused with 403 (the service should fall back to watching Jobs).</summary>
    public bool ForbidPodWatch { get; set; }
    public List<bool> Watches { get; } = [];

    public IAsyncEnumerable<string> WatchJobChangesAsync(string ns, string labelSelector, bool pods, CancellationToken ct = default)
    {
        lock (Watches) Watches.Add(pods);
        if (pods && ForbidPodWatch)
            throw new HttpRequestException("forbidden", null, System.Net.HttpStatusCode.Forbidden);
        return JobChanges.Reader.ReadAllAsync(ct);
    }

    public Task<bool> CreateJobAsync(string ns, K8sJobManifest manifest, CancellationToken ct = default)
    {
        CreateJobCalls++;
        OnCreateJob?.Invoke();
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
            return Task.FromResult<HarnessPodState?>(HarnessPodStates.Exited0);
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

internal sealed class FakeStorage : IHarnessStorage
{
    public ConcurrentDictionary<string, string> Objects { get; } = new();
    public List<string> Deleted { get; } = [];
    public HarnessCollection Collection { get; set; } = new(
        new HarnessResultContents(new HarnessEnvelope.Succeeded("""{"lines":3}"""), "{}", []), []);
    public Action<string>? OnPut { get; set; }

    public Task PutTextAsync(string key, string content, string contentType, CancellationToken ct = default)
    {
        Objects[key] = content;
        OnPut?.Invoke(key);
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

internal sealed class StubS3CredentialsProvider : ICredentialsProvider<S3Credentials>
{
    public Task<S3Credentials> GetCredentialsAsync(CancellationToken ct = default)
        => Task.FromResult(new S3Credentials("access", "secret"));
}
