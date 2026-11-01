namespace Olve.Pipelines.Kubernetes;

/// <summary>
/// Read-modify-write of one K8s Secret under its resourceVersion, so a writer of another key is never
/// overwritten: a write that lost the race reads again and reapplies its edit.
/// </summary>
public static class SecretEdits
{
    // Each lost race means another writer finished, so this is about how many may write at once.
    internal const int MaxTries = 32;

    /// <summary>
    /// Applies <paramref name="edit"/> to the current values (null when there is no Secret). It may run several
    /// times; a null result means nothing to change. No values left deletes the Secret. The result says whether anything was written.
    /// </summary>
    public static async Task<Result<bool>> EditSecretAsync(
        this IKubernetesClient client, string ns, string secretName,
        Func<IReadOnlyDictionary<string, string>?, Result<Dictionary<string, string>?>> edit, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < MaxTries; attempt++)
        {
            var current = await client.GetSecretRevisionAsync(ns, secretName, ct);
            if (edit(current?.Data).TryPickProblems(out var problems, out var next))
                return problems;
            if (next is null)
                return false;

            var written = current switch
            {
                null => next.Count == 0 || await client.CreateSecretAsync(ns, secretName, next, ct),
                _ when next.Count == 0 => await client.TryDeleteSecretAsync(ns, secretName, current.ResourceVersion, ct),
                _ => await client.TryUpdateSecretAsync(ns, secretName, next, current.ResourceVersion, ct),
            };
            if (written)
                return true;
        }

        return new ConflictProblem("Secret '{0}' kept changing under concurrent writes; try again.", secretName);
    }
}
