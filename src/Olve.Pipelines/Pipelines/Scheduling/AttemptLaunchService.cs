using System.Globalization;
using Olve.Pipelines.Pipelines.Configs;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Versions;

namespace Olve.Pipelines.Pipelines.Scheduling;

/// <summary>
/// Everything the harness executor (B5) needs to launch one attempt as a K8s Job, derived from the
/// attempt, its run and the run's pinned version — so the executor re-derives nothing.
/// </summary>
/// <param name="Step">The step as declared in the pinned manifest: image, kind, declared secrets
/// (inject each as a same-named env var via <c>secretKeyRef</c>), timeout, config.</param>
/// <param name="InputJson">The <c>input.json</c> to upload (compact JSON; the step's input keys, plus <c>$config</c>
/// from the run's pinned config when the step declares config).</param>
/// <param name="FileRefs">File refs in the input: one presigned GET per ref in <c>OLVE_FILE_URLS</c>
/// (key <see cref="AttemptFileRef.FileKey"/>, object <see cref="AttemptFileRef.StorageKey"/>).</param>
/// <param name="Environment">The service-provided harness variables (sdk/README.md "Environment").
/// The executor adds <c>OLVE_RESULT_URL</c>, <c>OLVE_FILE_URLS</c>, <c>OLVE_DIR</c>, <c>TMPDIR</c> and secrets.</param>
/// <param name="BinaryKey">Garage key of the version's compiled harness (<c>step.tar.gz</c>).</param>
/// <param name="AttemptPrefix">Garage prefix for this attempt's <c>output.json</c>, <c>files/</c> and <c>log</c>.</param>
public sealed record AttemptLaunchSpec(
    Attempt Attempt,
    Run Run,
    PipelineVersion Version,
    StepDescriptor Step,
    string InputJson,
    string? InputHash,
    IReadOnlyList<AttemptFileRef> FileRefs,
    IReadOnlyDictionary<string, string> Environment,
    string BinaryKey,
    string AttemptPrefix)
{
    /// <summary>The select cases the step sits in, from the run's effective plan (<c>OLVE_CASES</c>).</summary>
    public IReadOnlyList<string> Cases { get; init; } = [];

    /// <summary>The run's effective plan (callback URLs cover its steps).</summary>
    public RunPlan? Plan { get; init; }
}

/// <summary>Builds <see cref="AttemptLaunchSpec"/>s. Stateless query (transient).</summary>
public class AttemptLaunchService(RunService runs, AttemptService attempts, PipelineVersionService versions, RunPlanCache plans)
{
    public Result<AttemptLaunchSpec> Get(Id<Attempt> attemptId)
    {
        if (!attempts.TryGet(attemptId, out var attempt))
            return new NotFoundProblem("Attempt '{0}' not found.", attemptId);
        if (!runs.TryGet(attempt.RunId, out var run))
            return new NotFoundProblem("Run '{0}' of attempt '{1}' not found.", attempt.RunId, attemptId);
        if (!versions.TryGet(run.VersionId, out var version))
            return new NotFoundProblem("Version {0} of run #{1} not found.", run.VersionNumber, run.Number);
        if (plans.ForRun(run).TryPickProblems(out var problems, out var plan))
            return problems;
        if (!plan.TryGetStep(attempt.StepName, out var stage, out var step))
            return new ResultProblem("Version {0} has no step '{1}'.", run.VersionNumber, attempt.StepName);
        if (attempt.Input is not { } input)
            return new ResultProblem("Attempt {0} of step '{1}' in run #{2} has no input (it was not scheduled to run).",
                attempt.Number, attempt.StepName, run.Number);

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OLVE_PIPELINE"] = run.PipelineId.Value.Value.ToString("D", CultureInfo.InvariantCulture),
            ["OLVE_RUN_ID"] = run.Id.Value.Value.ToString("D", CultureInfo.InvariantCulture),
            ["OLVE_RUN"] = run.Number.ToString(CultureInfo.InvariantCulture),
            ["OLVE_ATTEMPT"] = attempt.Number.ToString(CultureInfo.InvariantCulture),
            ["OLVE_VERSION_STAMP"] = run.VersionStamp,
        };

        return new AttemptLaunchSpec(
            attempt, run, version, step, ConfigDelivery.WithConfig(input, step, run.Config), attempt.InputHash,
            AttemptFileRefs.Extract(input), environment, version.BinaryKey, StorageLayout.AttemptPrefix(attempt)) { Cases = stage.Cases, Plan = plan };
    }
}
