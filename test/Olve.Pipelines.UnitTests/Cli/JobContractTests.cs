using System.Text.Json;
using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Utilities.Paginations;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.UnitTests.Cli;

/// <summary>
/// The CLI's hand-rolled <c>Job</c> contract must read what the server actually sends for an attempt:
/// its status is polymorphic, and an unknown discriminator would break <c>pl job</c> at runtime.
/// </summary>
public class JobContractTests
{
    private static readonly JsonSerializerOptions ServerOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = AppJsonContext.Default,
    };

    public static IEnumerable<Func<AttemptStatus>> Statuses()
    {
        var t = DateTimeOffset.UtcNow;
        yield return () => new Scheduled(1);
        yield return () => new InProgress(t);
        yield return () => new Succeeded(t, t);
        yield return () => new Failed(t, t, "boom");
        yield return () => new Skipped("not prod", null, t);
        yield return () => new Cancelled(t, t);
        yield return () => new Obsolete(Olve.Utilities.Ids.Id.New<Attempt>());
    }

    [Test]
    [MethodDataSource(nameof(Statuses))]
    public async Task ServerAttempt_DeserializesIntoCliJob(AttemptStatus status)
    {
        var attempt = TestAttempts.Make(stepName: "deploy-beta", runNumber: 412, number: 2, status: status);
        var json = JsonSerializer.Serialize(attempt, ServerOptions);

        var job = JsonSerializer.Deserialize(json, CliJsonContext.Default.Job)!;

        await Assert.That(job.Id).IsEqualTo(attempt.Id.Value.Value);
        await Assert.That(job.StepName).IsEqualTo("deploy-beta");
        await Assert.That(job.RunNumber).IsEqualTo(412);
        await Assert.That(job.Number).IsEqualTo(2);
        await Assert.That(job.Status.Kind).IsEqualTo(status.Discriminator());
    }
}

/// <summary>
/// AOT guard: every response type of the run/attempt endpoints serializes through the source-gen
/// <see cref="AppJsonContext"/> alone (a missing registration throws <see cref="NotSupportedException"/>).
/// </summary>
public class RunApiSerializationTests
{
    private static readonly JsonSerializerOptions ServerOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = AppJsonContext.Default,
    };

    [Test]
    public async Task FullyPopulatedRun_RoundTripsThroughAppJsonContext()
    {
        var t = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        var run = new Run(
            Olve.Utilities.Ids.Id.New<Run>(), Olve.Utilities.Ids.Id.New<Pipeline>(), 412,
            Olve.Utilities.Ids.Id.New<PipelineVersion>(), 8, """{"sha":"abc"}""", "2026.10.21.412",
            RunStatus.Superseded, new RunTrigger(RunTriggerKind.Poll, "sha:abc"), t, t.AddMinutes(3),
            SupersededBy: Olve.Utilities.Ids.Id.New<Run>(), RerunOf: Olve.Utilities.Ids.Id.New<Run>());

        var json = JsonSerializer.Serialize(new[] { run }, ServerOptions);
        var back = JsonSerializer.Deserialize<Run[]>(json, ServerOptions)!;

        await Assert.That(back.Single()).IsEqualTo(run);
        await Assert.That(json).Contains("\"status\":\"Superseded\"");
        await Assert.That(json).Contains("\"kind\":\"Poll\"");
    }

    [Test]
    public async Task PageOfAttempts_SerializesThroughAppJsonContext()
    {
        var attempt = TestAttempts.Make(status: new AttemptStatus.Succeeded(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow))
            with { InputHash = "sha256:in", Output = new AttemptOutput("runs/x/1/build/1/output.json", "{}") };
        var page = new Page<Attempt>([attempt], 0, 50, 1);

        var json = JsonSerializer.Serialize(page, ServerOptions);

        await Assert.That(json).Contains("\"type\":\"succeeded\"");
        await Assert.That(json).Contains("output.json");
    }
}
