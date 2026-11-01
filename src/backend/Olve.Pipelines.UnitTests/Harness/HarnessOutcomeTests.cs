using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Results;

namespace Olve.Pipelines.UnitTests.Harness;

public class HarnessOutcomeTests
{
    private static readonly HarnessContainerState InitOk = new("olve-init", 0, "Completed", null);

    private static HarnessPodState Step(int exitCode, string? reason = null, string? jobReason = null)
        => new(jobReason, jobReason is null ? null : "details", InitOk, new HarnessContainerState("step", exitCode, reason, null));

    private static Result<HarnessResultContents> Archive(HarnessEnvelope envelope) => new HarnessResultContents(envelope, "{}", []);

    [Test]
    [Arguments(1, AttemptFailureKind.Handler, "the handler threw (exit 1)")]
    [Arguments(64, AttemptFailureKind.Usage, "unknown harness mode or step (exit 64) — binary and manifest disagree")]
    [Arguments(65, AttemptFailureKind.Input, "input.json missing or invalid for the step (exit 65)")]
    [Arguments(66, AttemptFailureKind.Output, "the step's output failed validation (exit 66)")]
    [Arguments(74, AttemptFailureKind.ResultUpload, "the result upload failed after retries (exit 74); output files are lost")]
    [Arguments(78, AttemptFailureKind.PipelineInvalid, "the pipeline is invalid (exit 78)")]
    [Arguments(130, AttemptFailureKind.Cancelled, "the handler was interrupted (exit 130)")]
    [Arguments(143, AttemptFailureKind.Cancelled, "the handler was interrupted (exit 143)")]
    [Arguments(137, AttemptFailureKind.OutOfMemory, "step container was killed (exit 137, SIGKILL) — out of memory?")]
    [Arguments(2, AttemptFailureKind.Unknown, "step container exited 2")]
    public async Task StepExitCodes_Map(int exitCode, AttemptFailureKind kind, string message)
    {
        await Assert.That(HarnessExitCodes.Classify(Step(exitCode))).IsEqualTo(new HarnessFailure(kind, message));
    }

    [Test]
    public async Task UnknownExitCode_NamesTheReason()
    {
        await Assert.That(HarnessExitCodes.Classify(Step(2, "Error"))!.Message).IsEqualTo("step container exited 2: Error");
    }

    [Test]
    public async Task ExitZero_IsNoFailure()
    {
        await Assert.That(HarnessExitCodes.Classify(Step(0))).IsNull();
    }

    [Test]
    public async Task OomKilledReason_Wins()
    {
        await Assert.That(HarnessExitCodes.Classify(Step(1, "OOMKilled")))
            .IsEqualTo(new HarnessFailure(AttemptFailureKind.OutOfMemory, "step container was killed (exit 1, OOMKilled) — out of memory?"));
    }

    [Test]
    public async Task DeadlineExceeded_FromJobCondition()
    {
        await Assert.That(HarnessExitCodes.Classify(Step(143, jobReason: "DeadlineExceeded")))
            .IsEqualTo(new HarnessFailure(AttemptFailureKind.DeadlineExceeded, "step timed out: details"));
        var pending = new HarnessPodState("DeadlineExceeded", null, null, null);
        await Assert.That(HarnessExitCodes.Classify(pending))
            .IsEqualTo(new HarnessFailure(AttemptFailureKind.DeadlineExceeded, "step timed out: Job was active longer than its deadline"));
    }

    [Test]
    [Arguments(64, "bad configuration (exit 64)")]
    [Arguments(65, "downloading the step binary or input failed (exit 65) — expired or wrong pre-signed URL?")]
    [Arguments(66, "the step archive is not a valid tar.gz with 'step' at its root (exit 66)")]
    [Arguments(70, "filesystem error (exit 70)")]
    [Arguments(9, "exit 9: Error")]
    public async Task InitExitCodes_AreInitFailures_NotStepMeanings(int exitCode, string meaning)
    {
        var state = new HarnessPodState(null, null, new HarnessContainerState("olve-init", exitCode, "Error", null), null);

        await Assert.That(HarnessExitCodes.Classify(state))
            .IsEqualTo(new HarnessFailure(AttemptFailureKind.InitFailed, $"olve-init failed: {meaning}"));
    }

    [Test]
    [Arguments("CreateContainerConfigError")]
    [Arguments("ImagePullBackOff")]
    [Arguments("ErrImagePull")]
    public async Task FatalWaiting_IsPodFailure(string reason)
    {
        var state = new HarnessPodState(null, null, InitOk, new HarnessContainerState("step", null, reason, "secret key SSH_KEY not found"));

        await Assert.That(HarnessExitCodes.Classify(state))
            .IsEqualTo(new HarnessFailure(AttemptFailureKind.PodFailed, $"container 'step' cannot start: {reason}: secret key SSH_KEY not found"));
    }

    [Test]
    public async Task JobFailedWithoutATerminatedContainer_NamesTheReason()
    {
        await Assert.That(HarnessExitCodes.Classify(new HarnessPodState("BackoffLimitExceeded", "too many", null, null)))
            .IsEqualTo(new HarnessFailure(AttemptFailureKind.PodFailed, "Job failed (BackoffLimitExceeded): too many"));
    }

    [Test]
    public async Task RunningPod_DidNotTerminate()
    {
        await Assert.That(HarnessExitCodes.Classify(new HarnessPodState(null, null, InitOk, null)))
            .IsEqualTo(new HarnessFailure(AttemptFailureKind.PodFailed, "step container did not terminate"));
    }

    [Test]
    public async Task PodStateFrom_K8sStatus()
    {
        var job = new K8sJobResponse(new K8sMetadata("j"), new K8sJobStatusBody(
            [new K8sJobCondition("Failed", "True", "Job was active longer than specified deadline", "DeadlineExceeded")], null, 1, null));
        var pod = new K8sPodItem(new K8sMetadata("p"), new K8sPodStatus("Failed", null,
            [new K8sContainerStatus("olve-init", new K8sContainerState(Terminated: new K8sContainerStateTerminated(0, "Completed")))],
            [new K8sContainerStatus("step", new K8sContainerState(Terminated: new K8sContainerStateTerminated(143, "Error")))]));

        var state = HarnessPodState.From(job, pod);

        await Assert.That(state.JobFailedReason).IsEqualTo("DeadlineExceeded");
        await Assert.That(state.Main!.ExitCode).IsEqualTo(143);
        await Assert.That(state.Init!.ExitCode).IsEqualTo(0);
    }

    [Test]
    public async Task Resolve_SucceededEnvelope()
    {
        var result = HarnessOutcome.Resolve(Step(0), Archive(new HarnessEnvelope.Succeeded("""{"a":1}""")), ["k1"], null);

        var succeeded = (HarnessAttemptResult.Succeeded)result;
        await Assert.That(succeeded.OutputJson).IsEqualTo("""{"a":1}""");
        await Assert.That(succeeded.FileKeys).IsEquivalentTo(new[] { "k1" });
    }

    [Test]
    public async Task Resolve_Skipped()
    {
        var result = HarnessOutcome.Resolve(Step(0), Archive(new HarnessEnvelope.Skipped("no change")), [], null);

        await Assert.That(result).IsEqualTo(new HarnessAttemptResult.Skipped("no change"));
    }

    [Test]
    public async Task Resolve_HandlerFailure_CarriesEnvelopeMessage()
    {
        var result = HarnessOutcome.Resolve(Step(1), Archive(new HarnessEnvelope.Failed("handler", "kaboom")), [], null);

        var failed = (HarnessAttemptResult.Failed)result;
        await Assert.That(failed.Kind).IsEqualTo(AttemptFailureKind.Handler);
        await Assert.That(failed.Message).IsEqualTo("the handler threw (exit 1): kaboom");
    }

    [Test]
    public async Task Resolve_Exit74_FailsEvenIfLogSaysSucceeded()
    {
        var result = HarnessOutcome.Resolve(Step(74), null, [], """{"output":null,"status":"succeeded"}""");

        await Assert.That(((HarnessAttemptResult.Failed)result).Kind).IsEqualTo(AttemptFailureKind.ResultUpload);
    }

    [Test]
    public async Task Resolve_NoArchive_FallsBackToLogMessage()
    {
        var result = HarnessOutcome.Resolve(Step(65), null, [], """{"error":{"kind":"input","message":"missing key sha"},"status":"failed"}""");

        var failed = (HarnessAttemptResult.Failed)result;
        await Assert.That(failed.Kind).IsEqualTo(AttemptFailureKind.Input);
        await Assert.That(failed.Message).IsEqualTo("input.json missing or invalid for the step (exit 65): missing key sha");
    }

    [Test]
    public async Task Resolve_ExitZeroWithoutArchive_IsResultInvalid()
    {
        var result = HarnessOutcome.Resolve(Step(0), null, [], """{"output":null,"status":"succeeded"}""");

        await Assert.That(result).IsEqualTo(new HarnessAttemptResult.Failed(AttemptFailureKind.ResultInvalid, "the step exited 0 but uploaded no result.tar.gz"));
    }

    [Test]
    public async Task Resolve_ExitZeroWithBadArchive_IsResultInvalid()
    {
        var bad = Result.Failure<HarnessResultContents>(new ResultProblem("Invalid result archive: no output.json"));
        var result = HarnessOutcome.Resolve(Step(0), bad, [], null);

        var failed = (HarnessAttemptResult.Failed)result;
        await Assert.That(failed.Kind).IsEqualTo(AttemptFailureKind.ResultInvalid);
        await Assert.That(failed.Message).IsEqualTo("Invalid result archive: no output.json");
    }
}
