using System.Collections.Concurrent;
using Olve.Pipelines.Shared.Persistence;

namespace Olve.Pipelines.UnitTests;

public sealed class MemorySnapshotStore : ISnapshotStore
{
    public ConcurrentDictionary<string, byte[]> Objects { get; } = new();

    public Task<byte[]?> TryReadAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(Objects.TryGetValue(key, out var v) ? v : null);

    public Task WriteAsync(string key, byte[] content, CancellationToken cancellationToken)
    {
        Objects[key] = content;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        Objects.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
