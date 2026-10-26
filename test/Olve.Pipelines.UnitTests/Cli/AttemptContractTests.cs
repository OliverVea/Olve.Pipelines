using System.Text.Json;
using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Versions;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.UnitTests.Cli;

/// <summary>
/// The CLI's hand-rolled <c>Attempt</c> contract must read what the server actually sends for an attempt:
/// its status is polymorphic, and an unknown discriminator would break <c>pl step</c>/<c>pl run</c> at runtime.
/// </summary>
public class AttemptContractTests
{
    private static readonly JsonSerializerOptions ServerOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = AppJsonContext.Default,
    };

    public static IEnumerable<Func<AttemptStatus>> Statuses()
    {
        var t = DateTimeOffset.UtcNow;
        yield return () => new Waiting(t);
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
    public async Task ServerAttempt_DeserializesIntoCliAttempt(AttemptStatus status)
    {
        var attempt = TestAttempts.Make(stepName: "deploy-beta", runNumber: 412, number: 2, status: status);
        var json = JsonSerializer.Serialize(attempt, ServerOptions);

        var job = JsonSerializer.Deserialize(json, CliJsonContext.Default.Attempt)!;

        await Assert.That(job.Id).IsEqualTo(attempt.Id.Value.Value);
        await Assert.That(job.StepName).IsEqualTo("deploy-beta");
        await Assert.That(job.RunNumber).IsEqualTo(412);
        await Assert.That(job.Number).IsEqualTo(2);
        await Assert.That(job.Status.Kind).IsEqualTo(status.Discriminator());
    }

    [Test]
    public async Task Answers_ReadIntoTheCliContract()
    {
        var t = DateTimeOffset.UtcNow;
        var attempt = TestAttempts.Make(stepName: "approve", status: new Succeeded(t, t)) with
        {
            Answers = new Dictionary<string, ExternalAnswer> { ["decision"] = new("""{"approved":true}""", "oliver", t) },
        };

        var cli = JsonSerializer.Deserialize(JsonSerializer.Serialize(attempt, ServerOptions), CliJsonContext.Default.Attempt)!;

        await Assert.That(cli.Answers!["decision"].AnsweredBy).IsEqualTo("oliver");
        await Assert.That(cli.Answers["decision"].Value).IsEqualTo("""{"approved":true}""");
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

        var json = JsonSerializer.Serialize(run, ServerOptions);
        var back = JsonSerializer.Deserialize<Run>(json, ServerOptions)!;

        await Assert.That(back).IsEqualTo(run);
        await Assert.That(json).Contains("\"status\":\"Superseded\"");
        await Assert.That(json).Contains("\"kind\":\"Poll\"");
    }

    [Test]
    public async Task ActiveAttempts_SerializeThroughAppJsonContext_AndReadIntoTheCliContract()
    {
        var t = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        var active = new ActiveAttempt(Olve.Utilities.Ids.Id.New<Pipeline>(), "Olve.Pipelines v2", 12, "deploy", 2, "in-progress", t, t.AddSeconds(5));

        var json = JsonSerializer.Serialize(new[] { active }, ServerOptions);
        var back = JsonSerializer.Deserialize(json, CliJsonContext.Default.ActiveAttemptArray)!;

        await Assert.That(back[0].PipelineName).IsEqualTo("Olve.Pipelines v2");
        await Assert.That(back[0].Run).IsEqualTo(12);
        await Assert.That(back[0].Step).IsEqualTo("deploy");
        await Assert.That(back[0].Attempt).IsEqualTo(2);
        await Assert.That(back[0].StartedAt).IsEqualTo(t.AddSeconds(5));
    }
}
