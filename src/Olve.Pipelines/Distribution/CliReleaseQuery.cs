using System.Diagnostics.CodeAnalysis;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;

namespace Olve.Pipelines.Distribution;

/// <summary>
/// Where the served <c>pl</c> CLI comes from (<c>Distribution:*</c>): the latest successful attempt of
/// <see cref="CliStep"/> in the pipeline named (or with the id) <see cref="CliPipeline"/>. Unset → nothing is
/// served. It is one configured pipeline, never "any pipeline with a publish-cli step": the download route is
/// anonymous, so any repo bound on the instance could otherwise serve its own binary as <c>pl</c>.
/// </summary>
public sealed record DistributionOptions(string? CliPipeline, string CliStep = "publish-cli")
{
    public static DistributionOptions From(IConfiguration configuration)
    {
        var s = configuration.GetSection("Distribution");
        var pipeline = s["CliPipeline"];
        var step = s["CliStep"];
        return new DistributionOptions(
            string.IsNullOrWhiteSpace(pipeline) ? null : pipeline.Trim(),
            string.IsNullOrWhiteSpace(step) ? "publish-cli" : step.Trim());
    }
}

/// <summary>
/// Resolves the attempt whose output files <c>GET /download/{asset}</c> serves. Retention keeps that attempt's
/// run (<see cref="Pipelines.Retention.RetentionInputs.PinnedRuns"/>), so what is served is always what is kept.
/// Stateless (transient).
/// </summary>
public class CliReleaseQuery(DistributionOptions options, PipelineService pipelines, RunQueryService runs)
{
    public bool TryGetLatest([NotNullWhen(true)] out Attempt? attempt)
    {
        attempt = null;
        return options.CliPipeline is { } configured
               && ResolvePipeline(configured) is { } pipeline
               && runs.TryGetLatestSucceededAcrossRuns(pipeline.Id, options.CliStep, out attempt);
    }

    /// <summary>By id, else by exact name — only when exactly one pipeline has it.</summary>
    private Pipeline? ResolvePipeline(string configured)
    {
        if (Id<Pipeline>.TryParse(configured, null, out var id))
            return pipelines.TryGet(id, out var byId) ? byId : null;

        var byName = pipelines.List().Where(p => string.Equals(p.Name, configured, StringComparison.Ordinal)).ToList();
        return byName.Count == 1 ? byName[0] : null;
    }
}
