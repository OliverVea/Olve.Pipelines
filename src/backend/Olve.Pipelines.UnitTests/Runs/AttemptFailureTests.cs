using System.Text.Json;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Shared.Persistence;

namespace Olve.Pipelines.UnitTests.Runs;

public class AttemptFailureTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;

    [Test]
    [Arguments(AttemptFailureKind.Handler, AttemptFailureCategory.Step)]
    [Arguments(AttemptFailureKind.Input, AttemptFailureCategory.Step)]
    [Arguments(AttemptFailureKind.Output, AttemptFailureCategory.Step)]
    [Arguments(AttemptFailureKind.ResultInvalid, AttemptFailureCategory.Step)]
    [Arguments(AttemptFailureKind.PipelineInvalid, AttemptFailureCategory.Step)]
    [Arguments(AttemptFailureKind.Unknown, AttemptFailureCategory.Step)]
    [Arguments(AttemptFailureKind.Usage, AttemptFailureCategory.Infrastructure)]
    [Arguments(AttemptFailureKind.ResultUpload, AttemptFailureCategory.Infrastructure)]
    [Arguments(AttemptFailureKind.Cancelled, AttemptFailureCategory.Infrastructure)]
    [Arguments(AttemptFailureKind.OutOfMemory, AttemptFailureCategory.Infrastructure)]
    [Arguments(AttemptFailureKind.InitFailed, AttemptFailureCategory.Infrastructure)]
    [Arguments(AttemptFailureKind.PodFailed, AttemptFailureCategory.Infrastructure)]
    [Arguments(AttemptFailureKind.JobLost, AttemptFailureCategory.Infrastructure)]
    [Arguments(AttemptFailureKind.LaunchFailed, AttemptFailureCategory.Infrastructure)]
    [Arguments(AttemptFailureKind.DeadlineExceeded, AttemptFailureCategory.Timeout)]
    [Arguments(AttemptFailureKind.NotRun, AttemptFailureCategory.NotAnOutcome)]
    [Arguments(AttemptFailureKind.Declined, AttemptFailureCategory.NotAnOutcome)]
    public async Task EveryKind_HasItsCategory(AttemptFailureKind kind, AttemptFailureCategory category)
    {
        await Assert.That(AttemptFailures.Category(kind)).IsEqualTo(category);
    }

    [Test]
    [Arguments(AttemptFailureKind.Handler, "the handler threw (exit 1): boom")]
    [Arguments(AttemptFailureKind.Handler, "output rejected: too big")]
    [Arguments(AttemptFailureKind.Handler, "the step exited 0 but uploaded no result.tar.gz")]
    [Arguments(AttemptFailureKind.Handler, null)]
    [Arguments(AttemptFailureKind.JobLost, "K8s Job 'olve-1' disappeared while the attempt was running")]
    [Arguments(AttemptFailureKind.JobLost, "K8s Job 'olve-1' no longer exists; the attempt's outcome is unknown")]
    [Arguments(AttemptFailureKind.LaunchFailed, "K8s submission failed after 5 attempts: refused")]
    [Arguments(AttemptFailureKind.LaunchFailed, "cannot launch: no binary")]
    [Arguments(AttemptFailureKind.LaunchFailed, "cannot build the step Job: bad spec")]
    [Arguments(AttemptFailureKind.Declined, "declined by oliver: not today")]
    [Arguments(AttemptFailureKind.NotRun, "input.json exceeds 262144 bytes")]
    [Arguments(AttemptFailureKind.NotRun, "Input of step 'build' exceeds 262144 bytes.")]
    public async Task WithoutARecordedKind_TheReasonTells(AttemptFailureKind kind, string? reason)
    {
        await Assert.That(AttemptFailures.KindOf(new AttemptStatus.Failed(At, At, reason))).IsEqualTo(kind);
    }

    [Test]
    public async Task ARecordedKind_WinsOverTheReason()
    {
        var failed = new AttemptStatus.Failed(At, At, "step timed out: my own message", AttemptFailureKind.Handler);

        await Assert.That(AttemptFailures.KindOf(failed)).IsEqualTo(AttemptFailureKind.Handler);
    }

    [Test]
    public async Task StoredAttempts_KeepTheirKind_AndOlderOnesStillLoad()
    {
        AttemptStatus status = new AttemptStatus.Failed(At, At, "gone", AttemptFailureKind.JobLost);
        var stored = JsonSerializer.Serialize(status, RunPersistenceJsonContext.Default.AttemptStatus);
        await Assert.That(stored).Contains("\"Kind\":\"JobLost\"");
        await Assert.That(JsonSerializer.Deserialize(stored, RunPersistenceJsonContext.Default.AttemptStatus)).IsEqualTo(status);

        const string older = """{"type":"failed","StartedAt":"2026-10-01T00:00:00+00:00","FinishedAt":"2026-10-01T00:00:00+00:00","Reason":"boom"}""";
        var loaded = (AttemptStatus.Failed)JsonSerializer.Deserialize(older, RunPersistenceJsonContext.Default.AttemptStatus)!;
        await Assert.That(loaded.Kind).IsNull();
        await Assert.That(loaded.Reason).IsEqualTo("boom");
    }

    /// <summary>The reason table must follow the harness's wording, or older attempts change category silently.</summary>
    [Test]
    public async Task HarnessFailureMessages_ReadBackAsTheirKind()
    {
        var initOk = new HarnessContainerState("olve-init", 0, "Completed", null);
        HarnessPodState Step(int exit, string? reason = null) => new(null, null, initOk, new HarnessContainerState("step", exit, reason, null));
        HarnessPodState[] states =
        [
            new("DeadlineExceeded", "too slow", initOk, null),
            new(null, null, new HarnessContainerState("olve-init", 65, "Error", null), null),
            new(null, null, initOk, new HarnessContainerState("step", null, "ImagePullBackOff", "no such image")),
            new("BackoffLimitExceeded", "gave up", initOk, null),
            new(null, null, initOk, null),
            Step(137, "OOMKilled"),
            Step(64),
            Step(74),
            Step(143),
            Step(1),
        ];

        foreach (var state in states)
        {
            var failure = HarnessExitCodes.Classify(state)!;
            await Assert.That(AttemptFailures.KindOf(new AttemptStatus.Failed(At, At, failure.Message))).IsEqualTo(failure.Kind);
        }
    }
}
