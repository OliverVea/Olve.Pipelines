using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Results;

namespace Olve.Pipelines.UnitTests.Harness;

public class HarnessOutcomeTests
{
    private static readonly HarnessContainerState InitOk = new("olve-init", 0, "Completed", null);

    private static HarnessPodState Step(int exitCode, string? reason = null, string? jobReason = null)
        => new(jobReason, jobReason is null ? null : "details", InitOk, new HarnessContainerState("step", exitCode, reason, null));

    private static Result<HarnessResultContents> Archive(HarnessEnvelope envelope) => new HarnessResultContents(envelope, "{}", []);

    [Test]
    [Arguments(1, HarnessFailureKind.Handler)]
    [Arguments(64, HarnessFailureKind.Usage)]
    [Arguments(65, HarnessFailureKind.Input)]
    [Arguments(66, HarnessFailureKind.Output)]
    [Arguments(74, HarnessFailureKind.ResultUpload)]
    [Arguments(78, HarnessFailureKind.PipelineInvalid)]
    [Arguments(130, HarnessFailureKind.Cancelled)]
    [Arguments(143, HarnessFailureKind.Cancelled)]
    [Arguments(137, HarnessFailureKind.OutOfMemory)]
    [Arguments(2, HarnessFailureKind.Unknown)]
    public async Task StepExitCodes_Map(int exitCode, HarnessFailureKind kind)
    {
        await Assert.That(HarnessExitCodes.Classify(Step(exitCode))!.Kind).IsEqualTo(kind);
    }

    [Test]
    public async Task ExitZero_IsNoFailure()
    {
        await Assert.That(HarnessExitCodes.Classify(Step(0))).IsNull();
    }

    [Test]
    public async Task OomKilledReason_Wins()
    {
        await Assert.That(HarnessExitCodes.Classify(Step(1, "OOMKilled"))!.Kind).IsEqualTo(HarnessFailureKind.OutOfMemory);
    }

    [Test]
    public async Task DeadlineExceeded_FromJobCondition()
    {
        await Assert.That(HarnessExitCodes.Classify(Step(143, jobReason: "DeadlineExceeded"))!.Kind).IsEqualTo(HarnessFailureKind.DeadlineExceeded);
        var pending = new HarnessPodState("DeadlineExceeded", null, null, null);
        await Assert.That(HarnessExitCodes.Classify(pending)!.Kind).IsEqualTo(HarnessFailureKind.DeadlineExceeded);
    }

    [Test]
    [Arguments(64)]
    [Arguments(65)]
    [Arguments(66)]
    [Arguments(70)]
    public async Task InitExitCodes_AreInitFailures_NotStepMeanings(int exitCode)
    {
        var state = new HarnessPodState(null, null, new HarnessContainerState("olve-init", exitCode, "Error", null), null);

        var failure = HarnessExitCodes.Classify(state)!;
        await Assert.That(failure.Kind).IsEqualTo(HarnessFailureKind.InitFailed);
        await Assert.That(failure.Message).StartsWith("olve-init failed");
    }

    [Test]
    [Arguments("CreateContainerConfigError")]
    [Arguments("ImagePullBackOff")]
    [Arguments("ErrImagePull")]
    public async Task FatalWaiting_IsPodFailure(string reason)
    {
        var state = new HarnessPodState(null, null, InitOk, new HarnessContainerState("step", null, reason, "secret key SSH_KEY not found"));

        var failure = HarnessExitCodes.Classify(state)!;
        await Assert.That(failure.Kind).IsEqualTo(HarnessFailureKind.PodFailed);
        await Assert.That(failure.Message).Contains("SSH_KEY");
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
        await Assert.That(failed.Kind).IsEqualTo(HarnessFailureKind.Handler);
        await Assert.That(failed.Message).EndsWith(": kaboom");
    }

    [Test]
    public async Task Resolve_Exit74_FailsEvenIfLogSaysSucceeded()
    {
        var result = HarnessOutcome.Resolve(Step(74), null, [], """{"output":null,"status":"succeeded"}""");

        await Assert.That(((HarnessAttemptResult.Failed)result).Kind).IsEqualTo(HarnessFailureKind.ResultUpload);
    }

    [Test]
    public async Task Resolve_NoArchive_FallsBackToLogMessage()
    {
        var result = HarnessOutcome.Resolve(Step(65), null, [], """{"error":{"kind":"input","message":"missing key sha"},"status":"failed"}""");

        var failed = (HarnessAttemptResult.Failed)result;
        await Assert.That(failed.Kind).IsEqualTo(HarnessFailureKind.Input);
        await Assert.That(failed.Message).Contains("missing key sha");
    }

    [Test]
    public async Task Resolve_ExitZeroWithoutArchive_IsResultInvalid()
    {
        var result = HarnessOutcome.Resolve(Step(0), null, [], """{"output":null,"status":"succeeded"}""");

        await Assert.That(((HarnessAttemptResult.Failed)result).Kind).IsEqualTo(HarnessFailureKind.ResultInvalid);
    }

    [Test]
    public async Task Resolve_ExitZeroWithBadArchive_IsResultInvalid()
    {
        var bad = Result.Failure<HarnessResultContents>(new ResultProblem("Invalid result archive: no output.json"));
        var result = HarnessOutcome.Resolve(Step(0), bad, [], null);

        var failed = (HarnessAttemptResult.Failed)result;
        await Assert.That(failed.Kind).IsEqualTo(HarnessFailureKind.ResultInvalid);
        await Assert.That(failed.Message).Contains("no output.json");
    }
}
