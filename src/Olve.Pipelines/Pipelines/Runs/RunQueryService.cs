using System.Diagnostics.CodeAnalysis;
using Olve.Pipelines.Pipelines.Versions;

namespace Olve.Pipelines.Pipelines.Runs;

/// <summary>One row of a step's history (spec §6.3): the attempt, its run, and the step's entity
/// version at the run's pinned pipeline version (null if the version is gone or lacks the step).</summary>
public record StepHistoryEntry(Run Run, Attempt Attempt, int? StepVersion);

/// <summary>Stateless composed queries over runs, attempts and versions (addressing, history).</summary>
public class RunQueryService(RunService runs, AttemptService attempts, PipelineVersionService versions)
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

    /// <summary>History of one step identity across runs and versions, newest first.</summary>
    public IReadOnlyList<StepHistoryEntry> GetStepHistory(Id<Pipeline> pipelineId, string stepName, int limit = 50)
    {
        var result = new List<StepHistoryEntry>();
        foreach (var attempt in attempts.ListByStepName(pipelineId, stepName))
        {
            if (result.Count >= limit)
                break;
            if (!runs.TryGet(attempt.RunId, out var run))
                continue;

            int? stepVersion = versions.TryGet(run.VersionId, out var version)
                               && version.StepVersions.TryGetValue(stepName, out var v)
                ? v
                : null;
            result.Add(new StepHistoryEntry(run, attempt, stepVersion));
        }

        return result;
    }
}
