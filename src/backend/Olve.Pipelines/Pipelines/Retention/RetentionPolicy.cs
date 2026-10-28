using System.Text;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Versions;

namespace Olve.Pipelines.Pipelines.Retention;

/// <summary>The thresholds one pipeline's runs and binaries are judged by: the instance's <see cref="RetentionOptions"/>, with the fields its manifest's <c>retention</c> sets replaced.</summary>
public sealed record RetentionPolicy(int KeepLatestRuns, TimeSpan MinRunAge, TimeSpan BuildGrace)
{
    public static RetentionPolicy Of(RetentionOptions options) => new(options.KeepLatestRuns, options.MinRunAge, options.BuildGrace);

    public RetentionPolicy Override(ManifestRetention? retention) => retention is null
        ? this
        : new RetentionPolicy(
            retention.KeepLatestRuns is { } keep ? (int)Math.Min(keep, int.MaxValue) : KeepLatestRuns,
            retention.MinRunAgeDays is { } age ? TimeSpan.FromDays(age) : MinRunAge,
            retention.BuildGraceDays is { } grace ? TimeSpan.FromDays(grace) : BuildGrace);
}

/// <summary>Per-pipeline policies from the manifest of the version new runs start on (active, or a drain's target). Pipelines without overrides are absent.</summary>
public static class RetentionPolicies
{
    public static IReadOnlyDictionary<Id<Pipeline>, RetentionPolicy> Resolve(
        RetentionOptions options,
        IReadOnlyList<PipelineVersion> versions,
        IReadOnlyDictionary<Id<Pipeline>, PipelineActivation> activations,
        Action<Id<Pipeline>, IReadOnlyList<ManifestIssue>>? onUnparsable = null)
    {
        var byId = versions.ToDictionary(v => v.Id);
        var fallback = RetentionPolicy.Of(options);
        var result = new Dictionary<Id<Pipeline>, RetentionPolicy>();
        foreach (var (pipelineId, activation) in activations)
        {
            var versionId = activation switch
            {
                PipelineActivation.Active a => a.VersionId,
                PipelineActivation.Draining d => d.ToVersionId,
                _ => default,
            };
            if (!byId.TryGetValue(versionId, out var version)) continue;
            if (!ManifestParser.TryParse(Encoding.UTF8.GetBytes(version.Manifest), out var manifest, out var issues))
            {
                onUnparsable?.Invoke(pipelineId, issues);
                continue;
            }
            if (manifest.Retention is { } retention)
                result[pipelineId] = fallback.Override(retention);
        }
        return result;
    }
}
