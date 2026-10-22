using Olve.Pipelines.Pipelines;

namespace Olve.Pipelines.Kubernetes;

/// <summary>
/// Copies secret values from one pipeline's K8s Secret (<c>olve-pipeline-{id:N}</c>) to another's on
/// the same instance, server-side: values never leave the cluster/app, are never returned and never
/// logged — only names and pipeline ids/names (plus who asked) are. Validates everything first and
/// writes the target once, so a multi-secret copy never half-applies.
/// </summary>
/// <remarks>
/// Anyone who can write the target pipeline can read a copied value from its steps; the copy is as
/// authenticated as any other secret mutation and audit-logged, nothing more.
/// </remarks>
public class PipelineSecretCopyService(
    PipelineService pipelines,
    IKubernetesClient kubernetes,
    KubernetesOptions options,
    ILogger<PipelineSecretCopyService> logger)
{
    public async Task<Result<CopySecretsResponse>> CopyAsync(
        Id<Pipeline> targetId, CopySecretsRequest request, string actor, CancellationToken ct = default)
    {
        if (!pipelines.TryGet(targetId, out var target))
            return new ResultProblem("Pipeline '{0}' not found.", targetId);
        if (!pipelines.TryGet(request.SourcePipelineId, out var source))
            return new ResultProblem("Source pipeline '{0}' not found.", request.SourcePipelineId);

        var sourceData = await kubernetes.GetSecretAsync(options.Namespace, PipelineSecrets.Name(source.Id), ct);
        if (sourceData is null || sourceData.Count == 0)
            return new ResultProblem("Source pipeline '{0}' has no secrets.", source.Name);

        var targetData = source.Id == target.Id
            ? new Dictionary<string, string>(sourceData)
            : await kubernetes.GetSecretAsync(options.Namespace, PipelineSecrets.Name(target.Id), ct);

        if (Plan(sourceData, targetData, request, source.Id == target.Id).TryPickProblems(out var problems, out var plan))
            return problems;

        var merged = targetData is null ? new Dictionary<string, string>() : new Dictionary<string, string>(targetData);
        foreach (var (from, to) in plan)
            merged[to] = sourceData[from];

        if (targetData is null)
            await kubernetes.CreateSecretAsync(options.Namespace, PipelineSecrets.Name(target.Id), merged, ct);
        else
            await kubernetes.UpdateSecretAsync(options.Namespace, PipelineSecrets.Name(target.Id), merged, ct);

        var copied = plan.Select(p => p.To).ToArray();
        logger.LogInformation(
            "Secret copy by {Actor}: {SourceNames} from pipeline {SourcePipelineName} ({SourcePipelineId}) to {TargetNames} on pipeline {TargetPipelineName} ({TargetPipelineId}), overwrite {Overwrite}",
            actor, string.Join(", ", plan.Select(p => p.From)), source.Name, source.Id, string.Join(", ", copied),
            target.Name, target.Id, request.Overwrite);

        return new CopySecretsResponse(copied);
    }

    /// <summary>
    /// Which source keys go to which target keys. Pure: no values are touched, only key sets. Fails when
    /// a requested source key is missing, a target key exists without <see cref="CopySecretsRequest.Overwrite"/>,
    /// or the copy would be a no-op onto itself.
    /// </summary>
    public static Result<IReadOnlyList<(string From, string To)>> Plan(
        IReadOnlyDictionary<string, string> source,
        IReadOnlyDictionary<string, string>? target,
        CopySecretsRequest request,
        bool samePipeline)
    {
        var names = request.Names is { Length: > 0 } ? request.Names : source.Keys.Order(StringComparer.Ordinal).ToArray();

        if (request.TargetName is not null)
        {
            if (names.Length != 1)
                return new ResultProblem("targetName needs exactly one secret name to copy.");
            if (string.IsNullOrWhiteSpace(request.TargetName))
                return new ResultProblem("targetName must not be empty.");
        }

        var problems = new List<ResultProblem>();
        var plan = new List<(string From, string To)>();
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            var to = request.TargetName ?? name;
            if (!source.ContainsKey(name))
                problems.Add(new ResultProblem("Source pipeline has no secret '{0}'.", name));
            else if (samePipeline && to == name)
                problems.Add(new ResultProblem("Copying secret '{0}' onto itself does nothing; pass a different target name.", name));
            else if (target is not null && target.ContainsKey(to) && !request.Overwrite)
                problems.Add(new ResultProblem("Target pipeline already has secret '{0}' (pass overwrite to replace it).", to));
            else
                plan.Add((name, to));
        }

        return problems.Count > 0 ? new ResultProblemCollection(problems) : plan;
    }
}

/// <summary>
/// Body for <c>POST /api/pipelines/{pipelineId}/secrets/copy</c>: copy secrets from
/// <see cref="SourcePipelineId"/> into the route's pipeline. <see cref="Names"/> null/empty copies
/// all of the source's secrets; <see cref="TargetName"/> renames (exactly one name only).
/// </summary>
public record CopySecretsRequest(
    Id<Pipeline> SourcePipelineId,
    string[]? Names = null,
    string? TargetName = null,
    bool Overwrite = false);

/// <summary>The target secret names written (never values).</summary>
public record CopySecretsResponse(string[] Copied);
