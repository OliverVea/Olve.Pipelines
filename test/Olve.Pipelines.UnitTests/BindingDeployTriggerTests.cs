using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Shared;
using Olve.Results;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests;

public class BindingDeployTriggerTests
{
    private static PipelineConfigBindingService CreateService()
        => new(new EntityStore<PipelineConfigBinding>([]), new IdProvider());

    private static T Pick<T>(Result<T> r) { r.TryPickProblems(out _, out var v); return v!; }

    [Test]
    public async Task Create_DefaultsToWebhook_AndGeneratesSecret()
    {
        var svc = CreateService();
        var binding = Pick(svc.Create(Id.New<Pipeline>(), "acme/widgets", "main", ".pipelines", "GITHUB_TOKEN"));

        await Assert.That(binding.DeployTrigger).IsEqualTo(BindingDeployTrigger.Webhook);
        await Assert.That(string.IsNullOrEmpty(binding.WebhookSecret)).IsFalse();
    }

    [Test]
    public async Task Create_PollMode_HasNoSecret()
    {
        var svc = CreateService();
        var binding = Pick(svc.Create(Id.New<Pipeline>(), "acme/widgets", "main", ".pipelines", "GITHUB_TOKEN", BindingDeployTrigger.Poll));

        await Assert.That(binding.DeployTrigger).IsEqualTo(BindingDeployTrigger.Poll);
        await Assert.That(binding.WebhookSecret).IsNull();
    }

    [Test]
    public async Task SetDeployTrigger_PollToWebhook_GeneratesSecretOnce()
    {
        var svc = CreateService();
        var pid = Id.New<Pipeline>();
        Pick(svc.Create(pid, "acme/widgets", "main", ".pipelines", "GITHUB_TOKEN", BindingDeployTrigger.Poll));

        var afterWebhook = Pick(svc.SetDeployTrigger(pid, BindingDeployTrigger.Webhook));
        await Assert.That(afterWebhook.DeployTrigger).IsEqualTo(BindingDeployTrigger.Webhook);
        await Assert.That(string.IsNullOrEmpty(afterWebhook.WebhookSecret)).IsFalse();

        // Switching to webhook-only keeps the same secret (no churn → no hook recreate).
        var secret = afterWebhook.WebhookSecret;
        var afterWebhookOnly = Pick(svc.SetDeployTrigger(pid, BindingDeployTrigger.WebhookOnly));
        await Assert.That(afterWebhookOnly.WebhookSecret).IsEqualTo(secret);
    }
}
