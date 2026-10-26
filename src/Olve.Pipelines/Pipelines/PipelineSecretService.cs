using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines;

/// <summary>The K8s Secret holding a pipeline's secret values.</summary>
public static class PipelineSecrets
{
    public static string Name(Id<Pipeline> pipelineId) => $"olve-pipeline-{pipelineId.Value.Value:N}";
}

/// <param name="Description">From the manifest's secret declarations (null when not declared there).</param>
/// <param name="DeclaredBy">Steps, failure handlers (<c>onFailure:&lt;step&gt;</c>), poll triggers (<c>trigger:&lt;name&gt;</c>)
/// and the binding (<c>binding</c>, its GitHub credentials key) of the active version that use it; empty = set but unused.</param>
/// <param name="IsSet">Null when K8s could not be read.</param>
/// <param name="Needed">Whether the pipeline as the current config runs it uses the secret: a secret the config can reference
/// (<c>{ secret: NAME }</c>) is needed only where the config references it; any other one where a step, handler, trigger or the
/// binding declares it. Null when the config can't be evaluated.</param>
/// <summary>Which of a pipeline's secrets have a value (K8s I/O, read before the activation gate runs).</summary>
public interface IPipelineSecretNames
{
    /// <summary>The set secret names, or null when they can't be read (Kubernetes unavailable).</summary>
    Task<IReadOnlySet<string>?> ListSetAsync(Id<Pipeline> pipelineId, CancellationToken ct = default);
}

public record PipelineSecretInfo(string Name, string? Description, string[] DeclaredBy, bool? IsSet, bool? Needed = null);

/// <summary>
/// Lists, sets and deletes a pipeline's secret values in its K8s Secret. The K8s client is resolved
/// lazily: when K8s is unconfigured its factory throws, which becomes "unknown" (list) or a problem.
/// Values are never returned or logged. Transient.
/// </summary>
/// <summary>Pipeline secrets live in Kubernetes, not a store: the services that write them raise this.</summary>
public class PipelineSecretEvents
{
    public Event<Id<Pipeline>> OnChanged { get; } = new();
}

public class PipelineSecretService(
    PipelineSecretEvents secretEvents,
    PipelineService pipelines,
    PipelineConfigBindingService bindings,
    PipelineActivationService activations,
    RunPlanCache plans,
    Configs.ConfigSnapshotService snapshots,
    IServiceProvider services,
    ILogger<PipelineSecretService> logger) : IPipelineSecretNames
{
    public async Task<IReadOnlySet<string>?> ListSetAsync(Id<Pipeline> pipelineId, CancellationToken ct = default)
    {
        try
        {
            var (client, ns) = Kubernetes();
            var values = await client.GetSecretAsync(ns, PipelineSecrets.Name(pipelineId), ct);
            return (values?.Keys ?? Enumerable.Empty<string>()).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the secrets of pipeline '{PipelineId}'", pipelineId);
            return null;
        }
    }

    public async Task<Result<PipelineSecretInfo[]>> ListAsync(Id<Pipeline> pipelineId, CancellationToken ct = default)
    {
        if (!pipelines.TryGet(pipelineId, out _))
            return new NotFoundProblem("Pipeline '{0}' not found.", pipelineId);

        var declared = Declared(pipelineId, out var configUse);
        Dictionary<string, string>? values = null;
        var known = true;
        try
        {
            var (client, ns) = Kubernetes();
            values = await client.GetSecretAsync(ns, PipelineSecrets.Name(pipelineId), ct);
        }
        catch (Exception ex)
        {
            known = false;
            logger.LogWarning(ex, "Could not read the secrets of pipeline '{PipelineId}'", pipelineId);
        }

        var names = declared.Keys.Union(values?.Keys ?? Enumerable.Empty<string>()).Order(StringComparer.Ordinal);
        return names.Select(n => new PipelineSecretInfo(
                n,
                declared.TryGetValue(n, out var d) ? d.Description : null,
                d?.Users.ToArray() ?? [],
                known ? values?.ContainsKey(n) ?? false : null,
                Needed(n, declared.ContainsKey(n), configUse)))
            .ToArray();
    }

    public async Task<Result> SetAsync(Id<Pipeline> pipelineId, string name, string value, CancellationToken ct = default)
    {
        if (!pipelines.TryGet(pipelineId, out _))
            return new NotFoundProblem("Pipeline '{0}' not found.", pipelineId);
        if (TryKubernetes() is not { } k8s)
            return new UnavailableProblem("Kubernetes is not configured or unreachable; pipeline secrets cannot be changed.");
        var (client, ns) = k8s;

        var secretName = PipelineSecrets.Name(pipelineId);
        var existing = await client.GetSecretAsync(ns, secretName, ct);
        if (existing is null)
        {
            await client.CreateSecretAsync(ns, secretName, new Dictionary<string, string> { [name] = value }, ct);
        }
        else
        {
            existing[name] = value;
            await client.UpdateSecretAsync(ns, secretName, existing, ct);
        }

        secretEvents.OnChanged.Invoke(pipelineId);
        return Result.Success();
    }

    public async Task<Result> DeleteAsync(Id<Pipeline> pipelineId, string name, CancellationToken ct = default)
    {
        if (!pipelines.TryGet(pipelineId, out _))
            return new NotFoundProblem("Pipeline '{0}' not found.", pipelineId);
        if (TryKubernetes() is not { } k8s)
            return new UnavailableProblem("Kubernetes is not configured or unreachable; pipeline secrets cannot be changed.");
        var (client, ns) = k8s;

        var secretName = PipelineSecrets.Name(pipelineId);
        var existing = await client.GetSecretAsync(ns, secretName, ct);
        if (existing is not null && existing.Remove(name))
        {
            if (existing.Count == 0)
                await client.DeleteSecretAsync(ns, secretName, ct);
            else
                await client.UpdateSecretAsync(ns, secretName, existing, ct);
            secretEvents.OnChanged.Invoke(pipelineId);
        }

        return Result.Success();
    }

    /// <summary>Removes a deleted pipeline's K8s Secret (retention never sweeps <c>olve-pipeline-*</c>).</summary>
    public async Task DeleteAllAsync(Id<Pipeline> pipelineId, CancellationToken ct = default)
    {
        try
        {
            var (client, ns) = Kubernetes();
            await client.DeleteSecretAsync(ns, PipelineSecrets.Name(pipelineId), ct);
            logger.LogInformation("Deleted K8s Secret {SecretName} of deleted pipeline '{PipelineId}'", PipelineSecrets.Name(pipelineId), pipelineId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete the K8s Secret of deleted pipeline '{PipelineId}'", pipelineId);
        }
    }

    private sealed record Declaration(string? Description, SortedSet<string> Users);

    private static bool? Needed(string name, bool declared, ConfigUse? use)
    {
        if (use is null)
            return null;
        if (use.Referenceable.Contains(name))
            return use.Referenced?.Contains(name);
        return declared;
    }

    /// <summary>Secrets the config schema can reference, and those the current config does (null when it doesn't validate).</summary>
    private sealed record ConfigUse(IReadOnlySet<string> Referenceable, IReadOnlySet<string>? Referenced);

    private Dictionary<string, Declaration> Declared(Id<Pipeline> pipelineId, out ConfigUse? configUse)
    {
        configUse = null;
        var result = new Dictionary<string, Declaration>(StringComparer.Ordinal);
        void Add(string name, string user, string? description = null)
        {
            if (!result.TryGetValue(name, out var d))
                result[name] = d = new Declaration(description, new SortedSet<string>(StringComparer.Ordinal));
            else if (description is not null && d.Description is null)
                result[name] = d = d with { Description = description };
            d.Users.Add(user);
        }

        if (activations.TryGet(pipelineId, out var activation)
            && !plans.ForCurrentConfig(activation switch
            {
                PipelineActivation.Active a => a.VersionId,
                PipelineActivation.Draining d => d.ToVersionId,
                _ => throw new InvalidOperationException("Unknown activation."),
            }, snapshots).TryPickProblems(out _, out var plan))
        {
            var manifest = plan.Manifest;
            var referenceable = manifest.Config.SelectMany(e => Manifests.ConfigSecretRefs.Declared(e.Schema)).ToHashSet(StringComparer.Ordinal);
            snapshots.TryGetLatest(pipelineId, out var latest);
            configUse = new ConfigUse(referenceable,
                Configs.ConfigValidation.Effective(manifest, snapshots.Document(latest)).TryPickProblems(out _, out var effective)
                    ? null
                    : Configs.ConfigValidation.ReferencedSecrets(manifest, effective).ToHashSet(StringComparer.Ordinal));
            var descriptions = manifest.Secrets.ToDictionary(s => s.Name, s => s.Description, StringComparer.Ordinal);
            foreach (var step in plan.Stages.SelectMany(s => s.Steps))
                foreach (var secret in step.Secrets)
                    Add(secret, step.Name, descriptions.GetValueOrDefault(secret));
            foreach (var handler in manifest.FailureHandlers)
                foreach (var secret in handler.Step.Secrets)
                    Add(secret, $"onFailure:{handler.Step.Name}", descriptions.GetValueOrDefault(secret));
            foreach (var trigger in manifest.Triggers.OfType<Manifests.PollTrigger>())
                foreach (var secret in trigger.Secrets)
                    Add(secret, $"trigger:{trigger.Name}", descriptions.GetValueOrDefault(secret));
        }

        if (!bindings.GetByPipelineId(pipelineId).TryPickProblems(out _, out var binding) && binding.CredentialsSecret is { Length: > 0 } key)
            Add(key, "binding");

        return result;
    }

    private (IKubernetesClient Client, string Namespace) Kubernetes()
        => (services.GetRequiredService<IKubernetesClient>(), services.GetRequiredService<KubernetesOptions>().Namespace);

    private (IKubernetesClient Client, string Namespace)? TryKubernetes()
    {
        try
        {
            return Kubernetes();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Kubernetes is unavailable for a secret change");
            return null;
        }
    }
}

/// <summary>Deletes a deleted pipeline's K8s Secret (fire-and-forget; failures are only logged).</summary>
public class PipelineSecretEventRegistration(PipelineEvents pipelineEvents, IServiceProvider sp) : IRunOnStartup
{
    public Result Run()
    {
        pipelineEvents.OnDeleted.Subscribe(e => _ = sp.GetRequiredService<PipelineSecretService>().DeleteAllAsync(e.Id));

        return Result.Success();
    }
}
