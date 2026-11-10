using Microsoft.Extensions.Logging;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;
using Olve.Results;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests;

public class PipelineSecretCopyServiceTests
{
    private const string Ns = "runners";

    internal sealed class FakeSecrets : IKubernetesClient
    {
        private readonly Dictionary<string, int> _versions = [];
        private int _version;

        public Dictionary<string, Dictionary<string, string>> Secrets { get; } = [];
        public List<string> Writes { get; } = [];

        /// <summary>Runs after each read of a revision: another writer, between a read and the write it leads to.</summary>
        public Action? AfterRead { get; set; }

        public Task<bool> CreateSecretAsync(string ns, string secretName, Dictionary<string, string> data, CancellationToken ct = default)
        {
            lock (Secrets)
            {
                if (Secrets.ContainsKey(secretName))
                    return Task.FromResult(false);
                Put("create", secretName, data);
                return Task.FromResult(true);
            }
        }

        public Task<Dictionary<string, string>?> GetSecretAsync(string ns, string secretName, CancellationToken ct = default)
        {
            lock (Secrets)
                return Task.FromResult(Secrets.TryGetValue(secretName, out var d) ? new Dictionary<string, string>(d) : null);
        }

        public Task<SecretRevision?> GetSecretRevisionAsync(string ns, string secretName, CancellationToken ct = default)
        {
            SecretRevision? revision;
            lock (Secrets)
                revision = Secrets.TryGetValue(secretName, out var d)
                    ? new SecretRevision(new Dictionary<string, string>(d), Version(secretName))
                    : null;
            AfterRead?.Invoke();
            return Task.FromResult(revision);
        }

        public Task UpdateSecretAsync(string ns, string secretName, Dictionary<string, string> data, CancellationToken ct = default)
        {
            lock (Secrets)
            {
                if (!Secrets.ContainsKey(secretName))
                    throw new HttpRequestException($"Failed to update K8s Secret '{secretName}': NotFound", null, System.Net.HttpStatusCode.NotFound);
                Put("update", secretName, data);
            }
            return Task.CompletedTask;
        }

        public Task<bool> TryUpdateSecretAsync(string ns, string secretName, Dictionary<string, string> data, string resourceVersion, CancellationToken ct = default)
        {
            lock (Secrets)
            {
                if (!Secrets.ContainsKey(secretName) || Version(secretName) != resourceVersion)
                    return Task.FromResult(false);
                Put("update", secretName, data);
                return Task.FromResult(true);
            }
        }

        public Task DeleteSecretAsync(string ns, string secretName, CancellationToken ct = default)
        {
            lock (Secrets)
            {
                Writes.Add($"delete {secretName}");
                Secrets.Remove(secretName);
            }
            return Task.CompletedTask;
        }

        public Task<bool> TryDeleteSecretAsync(string ns, string secretName, string resourceVersion, CancellationToken ct = default)
        {
            lock (Secrets)
            {
                if (Secrets.ContainsKey(secretName) && Version(secretName) != resourceVersion)
                    return Task.FromResult(false);
                Writes.Add($"delete {secretName}");
                Secrets.Remove(secretName);
                return Task.FromResult(true);
            }
        }

        private void Put(string verb, string secretName, Dictionary<string, string> data)
        {
            Writes.Add($"{verb} {secretName}");
            Secrets[secretName] = new(data);
            _versions[secretName] = ++_version;
        }

        private string Version(string secretName) => _versions.GetValueOrDefault(secretName).ToString();

        public Task<KubernetesJobStatus> GetJobStatusAsync(string ns, string jobName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<KubernetesJobStatus?> TryGetJobStatusAsync(string ns, string jobName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetPodLogsAsync(string ns, string jobName, string? container = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteJobAsync(string ns, string jobName, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class CapturingLogger : ILogger<PipelineSecretCopyService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
                foreach (var (_, value) in pairs)
                    Messages.Add(value is System.Collections.IEnumerable e and not string
                        ? string.Join(",", e.Cast<object>())
                        : value?.ToString() ?? "");
        }
    }

    private sealed record Fixture(PipelineSecretCopyService Service, FakeSecrets K8s, CapturingLogger Log, Pipeline Source, Pipeline Target,
        PipelineSecretExpiryService Expiries);

    private static Fixture Build()
    {
        var store = new EntityStore<Pipeline>([]);
        var pipelines = new PipelineService(store, new IdProvider());
        pipelines.Create("source").TryPickValue(out var source, out _);
        pipelines.Create("target").TryPickValue(out var target, out _);
        var k8s = new FakeSecrets();
        k8s.Secrets[PipelineSecrets.Name(source!.Id)] = new() { ["GITHUB_TOKEN"] = "gh-value-1", ["REGISTRY"] = "reg-value-2" };
        var log = new CapturingLogger();
        var expiries = new PipelineSecretExpiryService(new AttachmentStore<Pipeline, PipelineSecretExpiries>(store), TimeProvider.System);
        var service = new PipelineSecretCopyService(new PipelineSecretEvents(), pipelines, expiries, k8s,
            new KubernetesOptions(Ns), log);
        return new Fixture(service, k8s, log, source, target!, expiries);
    }

    private static Dictionary<string, string>? TargetSecret(Fixture f) =>
        f.K8s.Secrets.GetValueOrDefault(PipelineSecrets.Name(f.Target.Id));

    [Test]
    public async Task CopyOne_CreatesTargetSecret_AndLogsNamesNotValues()
    {
        var f = Build();

        var result = await f.Service.CopyAsync(f.Target.Id, new CopySecretsRequest(f.Source.Id, ["GITHUB_TOKEN"]), "oliver");

        await Assert.That(result).Succeeded();
        result.TryPickValue(out var response, out _);
        await Assert.That(response!.Copied).IsEquivalentTo(["GITHUB_TOKEN"]);
        await Assert.That(TargetSecret(f)!["GITHUB_TOKEN"]).IsEqualTo("gh-value-1");
        await Assert.That(TargetSecret(f)!.ContainsKey("REGISTRY")).IsFalse();

        var log = string.Join("\n", f.Log.Messages);
        await Assert.That(log).Contains("oliver");
        await Assert.That(log).Contains("GITHUB_TOKEN");
        await Assert.That(log).Contains("source");
        await Assert.That(log).Contains("target");
        await Assert.That(log).DoesNotContain("gh-value-1");
    }

    [Test]
    public async Task CopyAll_WhenNoNames_CopiesEverySecret_InOneWrite_KeepingExisting()
    {
        var f = Build();
        f.K8s.Secrets[PipelineSecrets.Name(f.Target.Id)] = new() { ["OWN"] = "own" };

        var result = await f.Service.CopyAsync(f.Target.Id, new CopySecretsRequest(f.Source.Id), "oliver");

        await Assert.That(result).Succeeded();
        await Assert.That(TargetSecret(f)!.Keys.Order()).IsEquivalentTo(["GITHUB_TOKEN", "OWN", "REGISTRY"]);
        await Assert.That(f.K8s.Writes).IsEquivalentTo([$"update {PipelineSecrets.Name(f.Target.Id)}"]);
    }

    [Test]
    public async Task TargetName_RenamesTheCopy()
    {
        var f = Build();

        var result = await f.Service.CopyAsync(f.Target.Id, new CopySecretsRequest(f.Source.Id, ["GITHUB_TOKEN"], "GH"), "oliver");

        await Assert.That(result).Succeeded();
        await Assert.That(TargetSecret(f)!["GH"]).IsEqualTo("gh-value-1");
        await Assert.That(TargetSecret(f)!.ContainsKey("GITHUB_TOKEN")).IsFalse();
    }

    [Test]
    public async Task ACopy_CarriesTheSourceExpiry_ToItsTargetName_AndOverwritingWithoutOneClearsIt()
    {
        var f = Build();
        var at = new DateTimeOffset(2027, 1, 25, 0, 0, 0, TimeSpan.Zero);
        f.Expiries.Set(f.Source.Id, "GITHUB_TOKEN", at);
        f.K8s.Secrets[PipelineSecrets.Name(f.Target.Id)] = new() { ["REGISTRY"] = "old" };
        f.Expiries.Set(f.Target.Id, "REGISTRY", at);

        await Assert.That(await f.Service.CopyAsync(f.Target.Id, new CopySecretsRequest(f.Source.Id, ["GITHUB_TOKEN"], "GH"), "oliver")).Succeeded();
        await Assert.That(await f.Service.CopyAsync(f.Target.Id, new CopySecretsRequest(f.Source.Id, ["REGISTRY"], Overwrite: true), "oliver")).Succeeded();

        await Assert.That(f.Expiries.Get(f.Target.Id, "GH")).IsEqualTo(at);
        await Assert.That(f.Expiries.Get(f.Target.Id, "REGISTRY")).IsNull();
    }

    [Test]
    public async Task ExistingTarget_WithoutOverwrite_Fails_AndWritesNothing()
    {
        var f = Build();
        f.K8s.Secrets[PipelineSecrets.Name(f.Target.Id)] = new() { ["GITHUB_TOKEN"] = "old" };

        var result = await f.Service.CopyAsync(f.Target.Id, new CopySecretsRequest(f.Source.Id), "oliver");

        await result.Problems.IsSingle<ConflictProblem>("Target pipeline already has secret 'GITHUB_TOKEN' (pass overwrite to replace it).");
        await Assert.That(f.K8s.Writes).IsEmpty();
        await Assert.That(TargetSecret(f)!["GITHUB_TOKEN"]).IsEqualTo("old");
    }

    [Test]
    public async Task ExistingTarget_WithOverwrite_Replaces()
    {
        var f = Build();
        f.K8s.Secrets[PipelineSecrets.Name(f.Target.Id)] = new() { ["GITHUB_TOKEN"] = "old" };

        var result = await f.Service.CopyAsync(f.Target.Id,
            new CopySecretsRequest(f.Source.Id, ["GITHUB_TOKEN"], Overwrite: true), "oliver");

        await Assert.That(result).Succeeded();
        await Assert.That(TargetSecret(f)!["GITHUB_TOKEN"]).IsEqualTo("gh-value-1");
    }

    [Test]
    public async Task MissingSourceSecret_Fails()
    {
        var f = Build();

        var result = await f.Service.CopyAsync(f.Target.Id, new CopySecretsRequest(f.Source.Id, ["NOPE"]), "oliver");

        await result.Problems.IsSingle<ResultProblem>("Source pipeline has no secret 'NOPE'.");
        await Assert.That(f.K8s.Writes).IsEmpty();
    }

    [Test]
    public async Task MissingSourcePipeline_Fails()
    {
        var f = Build();

        var source = Id.New<Pipeline>();

        var result = await f.Service.CopyAsync(f.Target.Id, new CopySecretsRequest(source, ["GITHUB_TOKEN"]), "oliver");

        await result.Problems.IsSingle<NotFoundProblem>($"Source pipeline '{source}' not found.");
    }

    [Test]
    public async Task SourceWithoutSecrets_Fails()
    {
        var f = Build();

        var result = await f.Service.CopyAsync(f.Source.Id, new CopySecretsRequest(f.Target.Id), "oliver");

        await result.Problems.IsSingle<ResultProblem>("Source pipeline 'target' has no secrets.");
    }

    [Test]
    public async Task TargetName_Blank_Fails()
    {
        var f = Build();

        var result = await f.Service.CopyAsync(f.Target.Id, new CopySecretsRequest(f.Source.Id, ["GITHUB_TOKEN"], TargetName: " "), "oliver");

        await result.Problems.IsSingle<ValidationProblem>("targetName: targetName must not be empty.");
    }

    [Test]
    public async Task TargetName_WithSeveralNames_Fails()
    {
        var f = Build();

        var result = await f.Service.CopyAsync(f.Target.Id, new CopySecretsRequest(f.Source.Id, TargetName: "X"), "oliver");

        await result.Problems.IsSingle<ValidationProblem>("targetName: targetName needs exactly one secret name to copy.");
    }

    [Test]
    public async Task SamePipeline_RequiresARename()
    {
        var f = Build();

        var onto = await f.Service.CopyAsync(f.Source.Id, new CopySecretsRequest(f.Source.Id, ["GITHUB_TOKEN"], Overwrite: true), "oliver");
        var renamed = await f.Service.CopyAsync(f.Source.Id, new CopySecretsRequest(f.Source.Id, ["GITHUB_TOKEN"], "GH2"), "oliver");

        await onto.Problems.IsSingle<ResultProblem>("Copying secret 'GITHUB_TOKEN' onto itself does nothing; pass a different target name.");
        await Assert.That(renamed).Succeeded();
        await Assert.That(f.K8s.Secrets[PipelineSecrets.Name(f.Source.Id)]["GH2"]).IsEqualTo("gh-value-1");
    }
}
