using Olve.Pipelines.Pipelines.Runs;

namespace Olve.Pipelines.Pipelines.Ci;

/// <summary>
/// Finds a succeeded attempt a CI step can reuse: same pipeline, same tested tree, same code (the definition's
/// <c>codeHash</c>), same step or cell. The step's input is left out on purpose: it carries the commit, and a tree
/// tested at another commit gives the same result. Stateless query over the stores (transient).
/// </summary>
public class CiReuseService(CiRunService runs, CiAttemptService attempts, CiPlanCache plans)
{
    public CiAttempt? FindOrigin(CiRun run, string? codeHash, string unitName)
    {
        if (codeHash is null)
            return null;
        foreach (var other in runs.ListByPipeline(run.PipelineId).Where(r => r.Tree == run.Tree && r.Id != run.Id && !r.IsClose).OrderByDescending(r => r.Number))
        {
            if (plans.Get(other).TryPickProblems(out _, out var plan) || plan.Manifest.CodeHash != codeHash)
                continue;
            if (attempts.TryGetLatestSucceeded(other.Id, unitName, out var succeeded) && attempts.Origin(succeeded) is { } origin)
                return origin;
        }

        return null;
    }
}
