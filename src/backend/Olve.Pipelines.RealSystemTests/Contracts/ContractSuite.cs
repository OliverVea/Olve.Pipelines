using System.Runtime.CompilerServices;

namespace Olve.Pipelines.RealSystemTests.Contracts;

/// <summary>
/// A contract runs the same assertions against a fake and the real system. A fake that doesn't hold a
/// rule lists it in <see cref="KnownDisagreements"/>: the rule then shows as skipped, with the reason, on
/// that fake alone, so a disagreement stays visible until someone settles it.
/// </summary>
public abstract class ContractSuite
{
    protected virtual IReadOnlyDictionary<string, string> KnownDisagreements { get; } = new Dictionary<string, string>();

    protected void Rule([CallerMemberName] string rule = "")
    {
        if (KnownDisagreements.TryGetValue(rule, out var reason))
            Skip.Test($"The fake disagrees with the real system: {reason}");
    }
}

/// <summary>What a contract runs against, for the length of one test.</summary>
public sealed record ContractSubject<T>(T Client, string Namespace, Func<string> NewName, IAsyncDisposable? Lease = null) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Lease?.DisposeAsync() ?? ValueTask.CompletedTask;
}
