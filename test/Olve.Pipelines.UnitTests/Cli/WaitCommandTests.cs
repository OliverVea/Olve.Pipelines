using Olve.Pipelines.Cli.Api.Contracts;
using Olve.Pipelines.Cli.Commands.Wait;

namespace Olve.Pipelines.UnitTests.Cli;

public class WaitCommandTests
{
    [Test]
    [Arguments("90s", 90)]
    [Arguments("30m", 1800)]
    [Arguments("2h", 7200)]
    [Arguments("45", 45)]
    public async Task Timeout_ParsesUnits(string text, int seconds) =>
        await Assert.That(Waiter.ParseTimeout(text).Value).IsEqualTo(TimeSpan.FromSeconds(seconds));

    [Test]
    public async Task Timeout_RejectsNonsense_AndAbsentMeansNone()
    {
        await Assert.That(Waiter.ParseTimeout("soon").Succeeded).IsFalse();
        await Assert.That(Waiter.ParseTimeout("-5m").Succeeded).IsFalse();
        await Assert.That(Waiter.ParseTimeout(null).Value).IsNull();
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
}
