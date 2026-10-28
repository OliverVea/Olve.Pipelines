using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.GitHub;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Sync;
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

        var (action, pipelineId, _) = receiver.Evaluate(binding.Id, body, sig, "push");

        await Assert.That(action).IsEqualTo(BindingWebhookAction.Deploy);
        await Assert.That(pipelineId).IsEqualTo(binding.PipelineId);
    }

    [Test]
    public async Task Evaluate_APush_AnnouncesItsCommitAndPushTime()
    {
        var (receiver, svc) = Create();
        var binding = Pick(svc.Create(Id.New<Pipeline>(), "acme/widgets", "main", ".pipelines", "GITHUB_TOKEN"));
        var body = Encoding.UTF8.GetBytes("""{"ref":"refs/heads/main","after":"abc123","repository":{"pushed_at":1761364800}}""");
        var sig = GitHubWebhookSignature.Compute(binding.WebhookSecret!, body);

        var (_, _, push) = receiver.Evaluate(binding.Id, body, sig, "push");

        await Assert.That(push).IsEqualTo(new AnnouncedPush("abc123", DateTimeOffset.FromUnixTimeSeconds(1761364800)));
    }

    [Test]
    public async Task Evaluate_PushToOtherBranch_Ignores()
    {
        var (receiver, svc) = Create();
        var binding = Pick(svc.Create(Id.New<Pipeline>(), "acme/widgets", "main", ".pipelines", "GITHUB_TOKEN"));
        var body = PushBody("dev");
        var sig = GitHubWebhookSignature.Compute(binding.WebhookSecret!, body);

        var (action, _, _) = receiver.Evaluate(binding.Id, body, sig, "push");

        await Assert.That(action).IsEqualTo(BindingWebhookAction.Ignore);
    }

    [Test]
    public async Task Evaluate_PingEvent_Ignores()
    {
        var (receiver, svc) = Create();
        var binding = Pick(svc.Create(Id.New<Pipeline>(), "acme/widgets", "main", ".pipelines", "GITHUB_TOKEN"));
        var body = Encoding.UTF8.GetBytes("""{"zen":"x"}""");
        var sig = GitHubWebhookSignature.Compute(binding.WebhookSecret!, body);

        var (action, _, _) = receiver.Evaluate(binding.Id, body, sig, "ping");

        await Assert.That(action).IsEqualTo(BindingWebhookAction.Ignore);
    }

    [Test]
    public async Task Evaluate_BadSignature_Rejected()
    {
        var (receiver, svc) = Create();
        var binding = Pick(svc.Create(Id.New<Pipeline>(), "acme/widgets", "main", ".pipelines", "GITHUB_TOKEN"));
        var body = PushBody("main");
        var sig = GitHubWebhookSignature.Compute("wrong", body);

        var (action, _, _) = receiver.Evaluate(binding.Id, body, sig, "push");

        await Assert.That(action).IsEqualTo(BindingWebhookAction.InvalidSignature);
    }

    [Test]
    public async Task Evaluate_UnknownBinding_NotFound()
    {
        var (receiver, _) = Create();
        var body = PushBody("main");

        var (action, _, _) = receiver.Evaluate(Id.New<PipelineConfigBinding>(), body, "sig", "push");

        await Assert.That(action).IsEqualTo(BindingWebhookAction.NotFound);
    }
}
