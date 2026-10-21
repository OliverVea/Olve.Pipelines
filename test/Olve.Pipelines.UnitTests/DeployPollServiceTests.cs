using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Sync.ConfigSource;
using Olve.Results;

namespace Olve.Pipelines.UnitTests;

public class DeployPollServiceTests
{
    private static T Pick<T>(Result<T> r) { r.TryPickProblems(out _, out var v); return v!; }

    private static (ServiceProvider Provider, GatedConfigSource Source) Build(int expectedCallers)
    {
        var source = new GatedConfigSource(expectedCallers);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddPipelineServices();
        services.AddSingleton<IConfigSource>(source);
        return (services.BuildServiceProvider(), source);
    }

    /// <summary>
    /// #39: the binding webhook and a second poll of the same binding (background loop or
    /// reconcile-now) both read the old deploy cursor before either advanced it. Both polls are held
    /// at the branch-head fetch so they overlap; the cursor must still land on the new head.
    /// </summary>
    [Test]
    public async Task ConcurrentPolls_OfOneNewHead_AdvanceCursorToHead()
    {
        var (provider, source) = Build(expectedCallers: 2);
        await using var _ = provider;

        var pipeline = Pick(provider.GetRequiredService<PipelineService>().Create("widgets"));
        var bindings = provider.GetRequiredService<PipelineConfigBindingService>();
        var binding = Pick(bindings.Create(pipeline.Id, "acme/widgets", "main", ".pipelines", "GITHUB_TOKEN"));
        Pick(bindings.SetLastDeployedSha(binding.Id, "head-0"));
        source.Inner.BranchHeadSha = "head-1"; // the push

        var poll = provider.GetRequiredService<DeployPollService>();
        var first = poll.ReconcileNowAsync(pipeline.Id, CancellationToken.None);
        var second = poll.ReconcileNowAsync(pipeline.Id, CancellationToken.None);

        await source.AllArrived.WaitAsync(TimeSpan.FromSeconds(10));
        source.Release();
        await Task.WhenAll(first, second);

        await Assert.That(Pick(bindings.TryGet(binding.Id)).LastDeployedSha).IsEqualTo("head-1");
    }

    /// <summary>Until v2 reconcile lands, a successful config fetch records an explicit not-implemented status.</summary>
    [Test]
    public async Task Poll_RecordsNotImplementedReconcileStatus()
    {
        var (provider, source) = Build(expectedCallers: 1);
        await using var _ = provider;
        source.Release();

        var pipeline = Pick(provider.GetRequiredService<PipelineService>().Create("widgets"));
        var bindings = provider.GetRequiredService<PipelineConfigBindingService>();
        var binding = Pick(bindings.Create(pipeline.Id, "acme/widgets", "main", ".pipelines", "GITHUB_TOKEN"));

        var result = await provider.GetRequiredService<DeployPollService>().ReconcileNowAsync(pipeline.Id, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        var status = Pick(bindings.TryGet(binding.Id)).Status;
        await Assert.That(status.Result).IsEqualTo(ReconcileResult.Error);
        await Assert.That(status.Problems).IsEquivalentTo([DeployPollService.NotImplementedProblem]);
    }

    /// <summary>Holds every branch-head fetch until all expected callers are waiting, then releases them together.</summary>
    private sealed class GatedConfigSource(int expectedCallers) : IConfigSource
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public FakeConfigSource Inner { get; } = new();
        public Task AllArrived => _allArrived.Task;
        public void Release() => _gate.TrySetResult();

        public async Task<Result<string>> GetBranchHeadShaAsync(PipelineConfigBinding binding, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _arrived) == expectedCallers)
                _allArrived.TrySetResult();

            await _gate.Task.WaitAsync(ct);
            return await Inner.GetBranchHeadShaAsync(binding, ct);
        }

        public Task<Result<ConfigFetch>> FetchConfigAsync(
            PipelineConfigBinding binding, string? etag = null, CancellationToken ct = default)
            => Inner.FetchConfigAsync(binding, etag, ct);
    }
}
