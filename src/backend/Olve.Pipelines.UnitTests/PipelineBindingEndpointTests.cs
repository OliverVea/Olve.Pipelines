using System.Text.Json;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Utilities.Ids;
using PipelinesJsonContext = Olve.Pipelines.Api.PipelinesJsonContext;

namespace Olve.Pipelines.UnitTests;

public class PipelineBindingEndpointTests
{
    [Test]
    public async Task BindingResponses_NeverCarryTheWebhookSecret()
    {
        var binding = new PipelineConfigBinding(Id.New<PipelineConfigBinding>(), Id.New<Pipeline>(), "acme/widgets", "main", ".pipelines",
            "GITHUB_TOKEN", null, null, ReconcileStatus.NeverRun, DateTimeOffset.UtcNow, WebhookSecret: "hmac-key");

        // The contract's binding has no such property, so no handler can leak it.
        var json = JsonSerializer.Serialize(BindingViews.ToApi(binding), PipelinesJsonContext.Default.PipelineBinding);

        await Assert.That(json).DoesNotContain("hmac-key");
        await Assert.That(json).DoesNotContain("webhookSecret");
        await Assert.That(json).Contains("\"repo\":\"acme/widgets\"");
    }
}
