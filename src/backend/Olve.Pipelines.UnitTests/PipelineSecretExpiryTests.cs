using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests;

public class PipelineSecretExpiryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Test]
    [Arguments(30, false, false)]
    [Arguments(15, false, false)]
    [Arguments(14, true, false)]
    [Arguments(1, true, false)]
    [Arguments(0, true, true)]
    [Arguments(-3, true, true)]
    public async Task ASecret_IsReported_FromFourteenDaysBeforeItExpires(int daysLeft, bool reported, bool expired)
    {
        var pipelines = new EntityStore<Pipeline>([]);
        var pipeline = new Pipeline(Id.New<Pipeline>(), "p");
        pipelines.Set(pipeline);
        var expiries = new PipelineSecretExpiryService(new AttachmentStore<Pipeline, PipelineSecretExpiries>(pipelines), new FixedTime());
        var at = Now.AddDays(daysLeft);

        expiries.Set(pipeline.Id, "TOKEN", at);

        await Assert.That(expiries.Expiring(pipeline.Id))
            .IsEquivalentTo(reported ? [new ExpiringSecret("TOKEN", at, expired)] : Array.Empty<ExpiringSecret>());
    }
}
