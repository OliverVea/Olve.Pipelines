using Olve.Pipelines.Jobs;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Shared;
using Olve.Results;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests;

public class BindingWebhookReceiverTests
{
    private static (BindingWebhookReceiver Receiver, PipelineConfigBindingService Svc) Create()
    {
        var svc = new PipelineConfigBindingService(new EntityStore<PipelineConfigBinding>([]), new IdProvider());
        return (new BindingWebhookReceiver(svc, NullLogger<BindingWebhookReceiver>.Instance), svc);
    }

    private static T Pick<T>(Result<T> r) { r.TryPickProblems(out _, out var v); return v!; }

    private static byte[] PushBody(string branch) => Encoding.UTF8.GetBytes($$"""{"ref":"refs/heads/{{branch}}"}""");

    [Test]
    public async Task Evaluate_PushToBoundBranch_Deploys()
    {
        var (receiver, svc) = Create();
        var binding = Pick(svc.Create(Id.New<Pipeline>(), "acme/widgets", "main", ".pipelines", "GITHUB_TOKEN"));
        var body = PushBody("main");
        var sig = GitHubWebhookSignature.Compute(binding.WebhookSecret!, body);

        var (action, pipelineId) = receiver.Evaluate(binding.Id, body, sig, "push");

        await Assert.That(action).IsEqualTo(BindingWebhookAction.Deploy);
        await Assert.That(pipelineId).IsEqualTo(binding.PipelineId);
    }

    [Test]
    public async Task Evaluate_PushToOtherBranch_Ignores()
    {
        var (receiver, svc) = Create();
        var binding = Pick(svc.Create(Id.New<Pipeline>(), "acme/widgets", "main", ".pipelines", "GITHUB_TOKEN"));
        var body = PushBody("dev");
        var sig = GitHubWebhookSignature.Compute(binding.WebhookSecret!, body);

        var (action, _) = receiver.Evaluate(binding.Id, body, sig, "push");

        await Assert.That(action).IsEqualTo(BindingWebhookAction.Ignore);
    }

    [Test]
    public async Task Evaluate_PingEvent_Ignores()
    {
        var (receiver, svc) = Create();
        var binding = Pick(svc.Create(Id.New<Pipeline>(), "acme/widgets", "main", ".pipelines", "GITHUB_TOKEN"));
        var body = Encoding.UTF8.GetBytes("""{"zen":"x"}""");
        var sig = GitHubWebhookSignature.Compute(binding.WebhookSecret!, body);

        var (action, _) = receiver.Evaluate(binding.Id, body, sig, "ping");

        await Assert.That(action).IsEqualTo(BindingWebhookAction.Ignore);
    }

    [Test]
    public async Task Evaluate_BadSignature_Rejected()
    {
        var (receiver, svc) = Create();
        var binding = Pick(svc.Create(Id.New<Pipeline>(), "acme/widgets", "main", ".pipelines", "GITHUB_TOKEN"));
        var body = PushBody("main");
        var sig = GitHubWebhookSignature.Compute("wrong", body);

        var (action, _) = receiver.Evaluate(binding.Id, body, sig, "push");

        await Assert.That(action).IsEqualTo(BindingWebhookAction.InvalidSignature);
    }

    [Test]
    public async Task Evaluate_UnknownBinding_NotFound()
    {
        var (receiver, _) = Create();
        var body = PushBody("main");

        var (action, _) = receiver.Evaluate(Id.New<PipelineConfigBinding>(), body, "sig", "push");

        await Assert.That(action).IsEqualTo(BindingWebhookAction.NotFound);
    }

    [Test]
    public async Task Evaluate_PollModeBinding_NotFound()
    {
        // Poll-mode bindings have no webhook secret, so the receiver treats them as not-a-webhook.
        var (receiver, svc) = Create();
        var binding = Pick(svc.Create(Id.New<Pipeline>(), "acme/widgets", "main", ".pipelines", "GITHUB_TOKEN", BindingDeployTrigger.Poll));
        var body = PushBody("main");

        var (action, _) = receiver.Evaluate(binding.Id, body, "sig", "push");

        await Assert.That(action).IsEqualTo(BindingWebhookAction.NotFound);
    }
}
