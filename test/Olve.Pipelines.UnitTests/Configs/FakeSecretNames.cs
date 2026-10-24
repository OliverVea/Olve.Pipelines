using Olve.Pipelines.Pipelines.Configs;
using Olve.Pipelines.Pipelines;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests.Configs;

/// <summary>A pipeline secret store that only knows names: <see cref="Set"/> lists the secrets that have a value.</summary>
internal sealed class FakeSecretNames : IPipelineSecretNames
{
    public HashSet<string> Set { get; } = new(StringComparer.Ordinal);

    public Task<IReadOnlySet<string>?> ListSetAsync(Id<Pipeline> pipelineId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlySet<string>?>(Set.ToHashSet(StringComparer.Ordinal));
}
