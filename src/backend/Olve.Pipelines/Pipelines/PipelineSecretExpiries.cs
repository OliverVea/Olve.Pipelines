using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines;

/// <summary>
/// When a pipeline's secrets expire, by secret name, as they were set. App state persisted in
/// <c>pipeline-state.json</c> beside the values in K8s: an edit of the K8s Secret outside the API doesn't update it.
/// </summary>
public sealed record PipelineSecretExpiries(Dictionary<string, DateTimeOffset> ExpiresAt);

/// <param name="Expired">True once <see cref="ExpiresAt"/> has passed.</param>
public record ExpiringSecret(string Name, DateTimeOffset ExpiresAt, bool Expired);

/// <summary>Keeps <see cref="PipelineSecretExpiries"/> in step with the secret values. Transient.</summary>
public class PipelineSecretExpiryService(AttachmentStore<Pipeline, PipelineSecretExpiries> expiries, TimeProvider time)
{
    /// <summary>How long before its expiry a secret is reported.</summary>
    public static readonly TimeSpan Warning = TimeSpan.FromDays(14);

    public DateTimeOffset? Get(Id<Pipeline> pipelineId, string name) =>
        expiries.TryGet(pipelineId, out var e) && e.ExpiresAt.TryGetValue(name, out var at) ? at : null;

    /// <summary>Records the expiry of a value just written; null clears it.</summary>
    public void Set(Id<Pipeline> pipelineId, string name, DateTimeOffset? expiresAt) =>
        Edit(pipelineId, next =>
        {
            if (expiresAt is { } at)
                next[name] = at;
            else
                next.Remove(name);
        });

    /// <summary>Carries each copied value's expiry from the source to its target name.</summary>
    public void Copy(Id<Pipeline> sourceId, Id<Pipeline> targetId, IEnumerable<(string From, string To)> copied)
    {
        var plan = copied.Select(c => (c.To, At: Get(sourceId, c.From))).ToList();
        Edit(targetId, next =>
        {
            foreach (var (to, at) in plan)
            {
                if (at is { } value)
                    next[to] = value;
                else
                    next.Remove(to);
            }
        });
    }

    /// <summary>Secrets expired or expiring within <see cref="Warning"/>, soonest first.</summary>
    public ExpiringSecret[] Expiring(Id<Pipeline> pipelineId)
    {
        if (!expiries.TryGet(pipelineId, out var e))
            return [];
        var now = time.GetUtcNow();
        return e.ExpiresAt
            .Where(kv => kv.Value - now <= Warning)
            .OrderBy(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new ExpiringSecret(kv.Key, kv.Value, kv.Value <= now))
            .ToArray();
    }

    private void Edit(Id<Pipeline> pipelineId, Action<Dictionary<string, DateTimeOffset>> change)
    {
        var next = expiries.TryGet(pipelineId, out var current)
            ? new Dictionary<string, DateTimeOffset>(current.ExpiresAt, StringComparer.Ordinal)
            : new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        change(next);
        if (current is not null && next.Count == current.ExpiresAt.Count && next.All(kv => current.ExpiresAt.TryGetValue(kv.Key, out var v) && v == kv.Value))
            return;
        if (next.Count == 0)
        {
            if (current is not null)
                expiries.Remove(pipelineId);
            return;
        }

        expiries.Set(pipelineId, new PipelineSecretExpiries(next));
    }
}
