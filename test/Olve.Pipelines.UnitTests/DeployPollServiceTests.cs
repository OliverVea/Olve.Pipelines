using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Sync.ConfigSource;
using Olve.Pipelines.Shared;
using Olve.Results;

namespace Olve.Pipelines.UnitTests;

public class DeployPollServiceTests
{
    private static T Pick<T>(Result<T> r) { r.TryPickProblems(out _, out var v); return v!; }

    /// <summary>
    /// #39: the binding webhook and a second poll of the same binding (background loop or
    /// reconcile-now) both read the old deploy cursor before either advanced it, and each fired a
    /// production run for one push. Both polls are held at the branch-head fetch so they overlap.
    /// </summary>
    [Test]
    public async Task ConcurrentPolls_OfOneNewHead_FireProductionOnce()
    {
        var source = new GatedConfigSource(expectedCallers: 2);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddPipelineServices();
        services.AddSingleton<IConfigSource>(source);
        await using var provider = services.BuildServiceProvider();

        var pipeline = Pick(provider.GetRequiredService<PipelineService>().Create("widgets"));
        var steps = provider.GetRequiredService<ProductionStepService>();
        var step = Pick(steps.Create(pipeline.Id, "build"));
        Pick(steps.SetConfiguration(step.Id, new StepConfiguration("alpine", "true")));

        var bindings = provider.GetRequiredService<PipelineConfigBindingService>();
        var binding = Pick(bindings.Create(pipeline.Id, "acme/widgets", "main", ".pipelines", "GITHUB_TOKEN"));
        Pick(bindings.SetLastSyncedSha(binding.Id, source.Inner.ConfigSha)); // config already applied
        Pick(bindings.SetLastDeployedSha(binding.Id, "head-0"));
        source.Inner.BranchHeadSha = "head-1"; // the push

        var poll = provider.GetRequiredService<DeployPollService>();
        var first = poll.ReconcileNowAsync(pipeline.Id, CancellationToken.None);
        var second = poll.ReconcileNowAsync(pipeline.Id, CancellationToken.None);

        await source.AllArrived.WaitAsync(TimeSpan.FromSeconds(10));
        source.Release();
        await Task.WhenAll(first, second);

        var groups = provider.GetRequiredService<JobGroupService>().List()
            .OfType<ProductionJobGroup>()
            .Count(g => g.PipelineId == pipeline.Id);

        await Assert.That(groups).IsEqualTo(1);
        await Assert.That(Pick(bindings.TryGet(binding.Id)).LastDeployedSha).IsEqualTo("head-1");
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
