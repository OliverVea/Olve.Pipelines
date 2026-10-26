using Olve.Pipelines.Cli;
using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Api.Contracts;
using Olve.Pipelines.Cli.Commands;
using Olve.Pipelines.Cli.Commands.Wait;
using Olve.Pipelines.Cli.Diagnostics;
using Olve.Pipelines.Cli.Output;
using Olve.Results;
using Olve.Results.TUnit;

namespace Olve.Pipelines.UnitTests.Cli;

public class WaitCommandTests
{
    [Test]
    [Arguments("90s", 90)]
    [Arguments("30m", 1800)]
    [Arguments("2h", 7200)]
    [Arguments("45", 45)]
    public async Task Timeout_ParsesUnits(string text, int seconds) =>
        await Assert.That(Waiter.ParseTimeout(text)).SucceededAndValue(v => v.IsEqualTo(TimeSpan.FromSeconds(seconds)));

    [Test]
    public async Task Timeout_RejectsNonsense_AndAbsentMeansNone()
    {
        await Assert.That(Waiter.ParseTimeout("soon").Succeeded).IsFalse();
        await Assert.That(Waiter.ParseTimeout("-5m").Succeeded).IsFalse();
        await Assert.That(Waiter.ParseTimeout(null)).SucceededAndValue(v => v.IsNull());
    }

    [Test]
    public async Task RunFinished_IsDoneOnlyWhenTerminal_AndSuccessOnlyWhenSucceeded()
    {
        static RunSummary Run(string status) => new() { Number = 7, Status = status };

        await Assert.That(Waiter.RunFinished("p", Run("Running"))).IsTypeOf<WaitCheck.Pending>();
        await Assert.That(Waiter.RunFinished("p", Run("Succeeded"))).IsEqualTo(new WaitCheck.Done("p#7  Succeeded", true));
        foreach (var status in new[] { "Failed", "Cancelled", "Superseded" })
            await Assert.That(((WaitCheck.Done)Waiter.RunFinished("p", Run(status))).Success).IsFalse();
    }

    private static (CliArgs Cli, CommandContext Ctx, StringWriter Stdout) Wait()
    {
        var stdout = new StringWriter();
        var ctx = new CommandContext
        {
            Json = false,
            Verbose = false,
            Api = new FakePipelinesApi(),
            Output = new ConsoleOutputWriter(false, stdout),
            Log = new StderrLog(false, TextWriter.Null),
            Config = new CliConfig(),
        };
        CliArgs.Parse(["wait"], new HashSet<string>(StringComparer.Ordinal), CliCommand.NoAliases).TryPickProblems(out _, out var cli);
        return (cli!, ctx, stdout);
    }

    private static Result<WaitCheck> Unavailable() =>
        Result.Failure<WaitCheck>(new ResultProblem("Request failed: 503 Service Unavailable") { IsRetryable = true });

    // Catches: `pl wait` failing a self-deploy because prod restarted while it was waiting.
    [Test]
    public async Task RetryableFailures_AreRetried_UntilTheConditionHolds()
    {
        var (cli, ctx, stdout) = Wait();
        var answers = new Queue<Result<WaitCheck>>([Unavailable(), Unavailable(), new WaitCheck.Done("p#7  Succeeded", true)]);
        var delays = new List<int>();

        var result = await Waiter.RunAsync(cli, ctx, "p", new HashSet<string>(), _ => Task.FromResult(answers.Dequeue()), CancellationToken.None,
            failures => { delays.Add(failures); return TimeSpan.Zero; });

        await Assert.That(result).Succeeded();
        await Assert.That(ctx.ExitCode).IsNull();
        await Assert.That(stdout.ToString()).Contains("p#7  Succeeded");
        await Assert.That(delays).IsEquivalentTo([1, 2]);
    }

    [Test]
    public async Task NonRetryableFailure_EndsTheWait()
    {
        var (cli, ctx, _) = Wait();
        var checks = 0;

        var result = await Waiter.RunAsync(cli, ctx, "p", new HashSet<string>(), _ =>
        {
            checks++;
            return Task.FromResult(Result.Failure<WaitCheck>(new ResultProblem("pipeline 'p' not found")));
        }, CancellationToken.None, _ => TimeSpan.Zero);

        await Assert.That(result).Failed();
        await Assert.That(checks).IsEqualTo(1);
    }

    [Test]
    public async Task RetryBackoff_GrowsAndIsCapped()
    {
        await Assert.That(Waiter.RetryBackoff(1)).IsEqualTo(TimeSpan.FromSeconds(2));
        await Assert.That(Waiter.RetryBackoff(3)).IsEqualTo(TimeSpan.FromSeconds(8));
        await Assert.That(Waiter.RetryBackoff(50)).IsEqualTo(TimeSpan.FromSeconds(30));
    }
}
