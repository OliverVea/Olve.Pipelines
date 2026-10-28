using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Results;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests;

public class PipelineBindingEndpointTests
{
    [Test]
    public async Task BindingResponses_NeverCarryTheWebhookSecret()
    {
        var binding = new PipelineConfigBinding(Id.New<PipelineConfigBinding>(), Id.New<Pipeline>(), "acme/widgets", "main", ".pipelines",
            "GITHUB_TOKEN", null, null, ReconcileStatus.NeverRun, DateTimeOffset.UtcNow, WebhookSecret: "hmac-key");

        var redacted = TestRunDomain.Pick(PipelineBindingEndpoints.Redacted(binding));

        await Assert.That(redacted.WebhookSecret).IsNull();
        await Assert.That(redacted).IsEqualTo(binding with { WebhookSecret = null });
        await Assert.That(PipelineBindingEndpoints.Redacted(new NotFoundProblem("gone"))).Failed();
    }
}
