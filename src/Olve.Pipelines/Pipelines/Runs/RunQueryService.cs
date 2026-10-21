using System.Diagnostics.CodeAnalysis;

namespace Olve.Pipelines.Pipelines.Runs;

/// <summary>Stateless composed queries over runs, attempts and versions (addressing).</summary>
public class RunQueryService(RunService runs, AttemptService attempts)
{
    /// <summary>
    /// Resolves an address <c>&lt;pipeline&gt;#&lt;run&gt;/&lt;step&gt;[@&lt;attempt&gt;]</c> (spec §5.5). Without an
    /// attempt it resolves to the latest successful attempt — the one later stages consume.
    /// </summary>
    public bool TryResolve(
        Id<Pipeline> pipelineId, int runNumber, string stepName, int? attemptNumber,
        [NotNullWhen(true)] out Attempt? attempt)
    {
        attempt = null;
        if (!runs.TryGetByNumber(pipelineId, runNumber, out var run))
            return false;

        return attemptNumber is { } n
            ? attempts.TryGetByNumber(run.Id, stepName, n, out attempt)
            : attempts.TryGetLatestSucceeded(run.Id, stepName, out attempt);
    }

    /// <summary>The latest successful attempt of a step across all runs (promote; later the environments view).</summary>
    public bool TryGetLatestSucceededAcrossRuns(Id<Pipeline> pipelineId, string stepName, [NotNullWhen(true)] out Attempt? attempt)
    {
        attempt = attempts.ListByStepName(pipelineId, stepName).FirstOrDefault(a => a.Status is AttemptStatus.Succeeded);
        return attempt is not null;
    }
}
