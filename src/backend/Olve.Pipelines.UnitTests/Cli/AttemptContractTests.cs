using System.Text.Json;
using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Versions;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;
using PipelinesJsonContext = Olve.Pipelines.Api.PipelinesJsonContext;

namespace Olve.Pipelines.UnitTests.Cli;

/// <summary>
/// The CLI's hand-rolled <c>Attempt</c> contract must read what the server actually sends for an attempt:
/// its status is polymorphic, and an unknown discriminator would break <c>pl step</c>/<c>pl run</c> at runtime.
/// </summary>
public class AttemptContractTests
{

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
        var json = JsonSerializer.Serialize(RunViews.ToApi(attempt), PipelinesJsonContext.Default.Attempt);

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

        var cli = JsonSerializer.Deserialize(JsonSerializer.Serialize(RunViews.ToApi(attempt), PipelinesJsonContext.Default.Attempt), CliJsonContext.Default.Attempt)!;

        await Assert.That(cli.Answers!["decision"].AnsweredBy).IsEqualTo("oliver");
        await Assert.That(cli.Answers["decision"].Value).IsEqualTo("""{"approved":true}""");
    }
}

/// <summary>
/// The contract's attempt views serialize through the generated context and read into the CLI's contract.
/// </summary>
public class RunApiSerializationTests
{
    [Test]
    public async Task ActiveAttempts_SerializeThroughTheContract_AndReadIntoTheCliContract()
    {
        var t = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        var active = new ActiveAttempt(Olve.Utilities.Ids.Id.New<Pipeline>(), "Olve.Pipelines v2", 12, "deploy", 2, "in-progress", t, t.AddSeconds(5));

        var json = JsonSerializer.Serialize<IReadOnlyList<Olve.Pipelines.Api.ActiveAttempt>>([RunViews.ToApi(active)], PipelinesJsonContext.Default.IReadOnlyListActiveAttempt);
        var back = JsonSerializer.Deserialize(json, CliJsonContext.Default.ActiveAttemptArray)!;

        await Assert.That(back[0].PipelineName).IsEqualTo("Olve.Pipelines v2");
        await Assert.That(back[0].Run).IsEqualTo(12);
        await Assert.That(back[0].Step).IsEqualTo("deploy");
        await Assert.That(back[0].Attempt).IsEqualTo(2);
        await Assert.That(back[0].StartedAt).IsEqualTo(t.AddSeconds(5));
    }
}
