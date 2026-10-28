using System.Text.Json.Nodes;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.UnitTests.Manifests;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.UnitTests.Scheduling;

/// <summary>
/// A pipeline with the self-deploy fixture manifest (or a mutation of it) as its active version, the
/// scheduler wired, and helpers that play the executor's part (InProgress → Succeeded/Failed).
/// Self-deploy stages: checkout | build, test | deploy-beta | when deployTo=prod: deploy-prod |
/// when publishCli: publish-cli | summary (beta required, prod/cli optional).
/// </summary>
internal sealed class SchedulerScenario
{
    public static readonly string Sha = new('a', 40);
    public static readonly string OtherSha = new('b', 40);

    public SchedulerScenario(string? manifest = null)
    {
        Clock = new MonotonicTimeProvider();
        D = new TestRunDomain(Clock).WireEvents(scheduler: true);
        Pipeline = D.CreatePipeline("olve-pipelines");
        Version = AddVersion(manifest);
        TestRunDomain.Pick(D.Switches.Switch(Pipeline.Id, Version.Id));
    }

    public MonotonicTimeProvider Clock { get; }
    public TestRunDomain D { get; }
    public Pipeline Pipeline { get; }
    public PipelineVersion Version { get; }

    public PipelineVersion AddVersion(string? manifest = null)
        => D.AddVersion(Pipeline.Id, manifest ?? ManifestFixture.Text, hash: "sha256:" + Guid.NewGuid().ToString("N"));

    /// <summary>The fixture with a mutation applied, as manifest text.</summary>
    public static string Mutated(Action<JsonObject> mutate)
    {
        var node = ManifestFixture.Node();
        mutate(node);
        return node.ToJsonString();
    }

    public static string Input(string deployTo = "beta", bool publishCli = true, string? sha = null)
        => $$"""{"deployTo":"{{deployTo}}","publishCli":{{(publishCli ? "true" : "false")}},"repo":"OliverVea/Olve.Pipelines","sha":"{{sha ?? Sha}}"}""";

    public RunStartOutcome StartOutcome(string input, RunTriggerKind kind = RunTriggerKind.Manual, string? dedupKey = null)
        => TestRunDomain.Pick(D.Starts.Start(Pipeline.Id, input, new RunTrigger(kind, dedupKey)));

    public Run Start(string input, RunTriggerKind kind = RunTriggerKind.Manual)
        => StartOutcome(input, kind) is RunStartOutcome.Started started
            ? started.Run
            : throw new InvalidOperationException("run was not started");

    public Run Get(Run run) => D.Runs.TryGet(run.Id, out var current) ? current : throw new InvalidOperationException("run is gone");

    public Attempt? Latest(Run run, string step) => D.AttemptService.ListForStep(run.Id, step).LastOrDefault();

    public IReadOnlyList<Attempt> All(Run run, string step) => D.AttemptService.ListForStep(run.Id, step);

    /// <summary>Steps with an active (Scheduled/InProgress) attempt in the run, sorted.</summary>
    public IReadOnlyList<string> Active(Run run)
        => D.AttemptService.ListByRun(run.Id).Where(a => a.Status.IsActive()).Select(a => a.StepName).Order(StringComparer.Ordinal).ToList();

    public Attempt MarkInProgress(Run run, string step)
    {
        var attempt = Latest(run, step) ?? throw new InvalidOperationException($"no attempt of '{step}'");
        TestRunDomain.Pick(D.AttemptService.MarkInProgress(attempt.Id));
        return D.Get(attempt.Id);
    }

    public Attempt Succeed(Run run, string step, string? output = null)
    {
        var attempt = Latest(run, step) ?? throw new InvalidOperationException($"no attempt of '{step}'");
        if (!attempt.Status.IsActive())
            throw new InvalidOperationException($"latest attempt of '{step}' is {attempt.Status.Discriminator()}");
        TestRunDomain.Pick(D.AttemptService.MarkInProgress(attempt.Id));
        if (D.AttemptService.MarkSucceeded(attempt.Id, output ?? DefaultOutput(Get(run).Number, step, attempt.Number)).TryPickProblems(out var problems))
            throw new InvalidOperationException(string.Join("; ", problems.Select(p => p.ToString())));
        return D.Get(attempt.Id);
    }

    public Attempt Fail(Run run, string step, string reason = "boom")
    {
        var attempt = Latest(run, step) ?? throw new InvalidOperationException($"no attempt of '{step}'");
        TestRunDomain.Pick(D.AttemptService.MarkInProgress(attempt.Id));
        if (D.AttemptService.MarkFailed(attempt.Id, reason).TryPickProblems(out var problems))
            throw new InvalidOperationException(string.Join("; ", problems.Select(p => p.ToString())));
        return D.Get(attempt.Id);
    }

    /// <summary>checkout, build + test, deploy-beta.</summary>
    public void SucceedThroughDeployBeta(Run run)
    {
        Succeed(run, "checkout");
        Succeed(run, "build");
        Succeed(run, "test");
        Succeed(run, "deploy-beta");
    }

    public static string FileRef(int run, string step, int attempt, string path)
        => $$"""{"attempt":{{attempt}},"path":"{{path}}","run":{{run}},"sha256":"{{new string('0', 64)}}","step":"{{step}}"}""";

    public static string? DefaultOutput(int run, string step, int attempt) => step switch
    {
        "checkout" => $$"""{"repo":"OliverVea/Olve.Pipelines","sha":"{{Sha}}","tree":{{FileRef(run, step, attempt, "tree/")}}}""",
        "build" => $$"""{"chart":{{FileRef(run, step, attempt, "chart/")}},"image":{{FileRef(run, step, attempt, "image.tar")}},"version":"{{attempt}}"}""",
        "test" => """{"failed":0,"passed":3}""",
        "deploy-beta" => $$"""{"release":"beta","url":"https://beta.example","version":"{{attempt}}"}""",
        "deploy-prod" => $$"""{"release":"prod","url":"https://prod.example","version":"{{attempt}}"}""",
        "publish-cli" => """{"assets":["pl"],"tag":"v1"}""",
        "summary" => null,
        _ => "{}",
    };

    public static bool IsSchedulerSkip(Attempt attempt, out string reason)
    {
        reason = attempt.Status is Skipped s ? s.Reason : "";
        return attempt.IsSchedulerSkip();
    }

    public static JsonNode? Json(string? text) => text is null ? null : JsonNode.Parse(text);
}
