using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;

namespace Olve.Pipelines.Jobs;

/// <summary>A matrix step's <c>maxParallel</c>, for the dispatcher: at most that many of its cells in progress per run. Transient.</summary>
public class MatrixParallelism(RunService runs, RunPlanCache plans)
{
    /// <summary>The limit of a cell attempt's matrix step; null for a plain step or a matrix without one.</summary>
    public long? LimitOf(Attempt attempt)
    {
        if (!MatrixCells.TryParseName(attempt.StepName, out _)
            || !runs.TryGet(attempt.RunId, out var run)
            || plans.ForRun(run).TryPickProblems(out _, out var plan))
            return null;
        return plan.TryGetUnit(attempt.StepName, out _, out var unit) ? unit.Step.Matrix?.MaxParallel : null;
    }

    /// <summary>What <c>maxParallel</c> counts against: the run and the matrix step.</summary>
    public static (Id<Run> Run, string Step) GroupOf(Attempt attempt) => (attempt.RunId, MatrixCells.StepOf(attempt.StepName));
}
