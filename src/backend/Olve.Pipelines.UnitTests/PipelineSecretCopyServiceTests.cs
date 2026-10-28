using Microsoft.Extensions.Logging;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Pipelines;
using Olve.Results;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests;

public class PipelineSecretCopyServiceTests
{
    private const string Ns = "runners";

    internal sealed class FakeSecrets : IKubernetesClient
    {
        public Dictionary<string, Dictionary<string, string>> Secrets { get; } = [];
        public List<string> Writes { get; } = [];

        public Task CreateSecretAsync(string ns, string secretName, Dictionary<string, string> data, CancellationToken ct = default)
        {
            Writes.Add($"create {secretName}");
            Secrets[secretName] = new(data);
            return Task.CompletedTask;
        }

        public Task<Dictionary<string, string>?> GetSecretAsync(string ns, string secretName, CancellationToken ct = default) =>
            Task.FromResult(Secrets.TryGetValue(secretName, out var d) ? new Dictionary<string, string>(d) : null);

        public Task UpdateSecretAsync(string ns, string secretName, Dictionary<string, string> data, CancellationToken ct = default)
        {
            Writes.Add($"update {secretName}");
            Secrets[secretName] = new(data);
            return Task.CompletedTask;
        }

        public Task DeleteSecretAsync(string ns, string secretName, CancellationToken ct = default)
        {
            Writes.Add($"delete {secretName}");
            Secrets.Remove(secretName);
            return Task.CompletedTask;
        }
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

    private sealed record Fixture(PipelineSecretCopyService Service, FakeSecrets K8s, CapturingLogger Log, Pipeline Source, Pipeline Target);

    private static Fixture Build()
    {
        var store = new EntityStore<Pipeline>([]);
        var pipelines = new PipelineService(store, new IdProvider());
        pipelines.Create("source").TryPickValue(out var source, out _);
        pipelines.Create("target").TryPickValue(out var target, out _);
        var k8s = new FakeSecrets();
        k8s.Secrets[PipelineSecrets.Name(source!.Id)] = new() { ["GITHUB_TOKEN"] = "gh-value-1", ["REGISTRY"] = "reg-value-2" };
        var log = new CapturingLogger();
        var service = new PipelineSecretCopyService(new PipelineSecretEvents(), pipelines, k8s,
            new KubernetesOptions(Ns), log);
        return new Fixture(service, k8s, log, source, target!);
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
    public async Task ExistingTarget_WithoutOverwrite_Fails_AndWritesNothing()
    {
        var f = Build();
        f.K8s.Secrets[PipelineSecrets.Name(f.Target.Id)] = new() { ["GITHUB_TOKEN"] = "old" };

        var result = await f.Service.CopyAsync(f.Target.Id, new CopySecretsRequest(f.Source.Id), "oliver");

        await Assert.That(result).Failed();
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

        await Assert.That(result).Failed();
        await Assert.That(f.K8s.Writes).IsEmpty();
    }

    [Test]
    public async Task MissingSourcePipeline_Fails()
    {
        var f = Build();

        var result = await f.Service.CopyAsync(f.Target.Id,
            new CopySecretsRequest(Id.New<Pipeline>(), ["GITHUB_TOKEN"]), "oliver");

        await Assert.That(result).Failed();
    }

    [Test]
    public async Task SourceWithoutSecrets_Fails()
    {
        var f = Build();

        var result = await f.Service.CopyAsync(f.Source.Id, new CopySecretsRequest(f.Target.Id), "oliver");

        await Assert.That(result).Failed();
    }

    [Test]
    public async Task TargetName_WithSeveralNames_Fails()
    {
        var f = Build();

        var result = await f.Service.CopyAsync(f.Target.Id, new CopySecretsRequest(f.Source.Id, TargetName: "X"), "oliver");

        await Assert.That(result).Failed();
    }

    [Test]
    public async Task SamePipeline_RequiresARename()
    {
        var f = Build();

        var onto = await f.Service.CopyAsync(f.Source.Id, new CopySecretsRequest(f.Source.Id, ["GITHUB_TOKEN"], Overwrite: true), "oliver");
        var renamed = await f.Service.CopyAsync(f.Source.Id, new CopySecretsRequest(f.Source.Id, ["GITHUB_TOKEN"], "GH2"), "oliver");

        await Assert.That(onto).Failed();
        await Assert.That(renamed).Succeeded();
        await Assert.That(f.K8s.Secrets[PipelineSecrets.Name(f.Source.Id)]["GH2"]).IsEqualTo("gh-value-1");
    }
}
