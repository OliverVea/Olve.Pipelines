using System.Text;
using Olve.Pipelines.Pipelines.Triggers;

namespace Olve.Pipelines.UnitTests;

public class GitHubWebhookSignatureTests
{
    // GitHub's documented example: secret "It's a Secret to Everybody", body "Hello, World!".
    // https://docs.github.com/en/webhooks/using-webhooks/validating-webhook-deliveries
    private const string Secret = "It's a Secret to Everybody";
    private const string Body = "Hello, World!";
    private const string Expected = "sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17";

    [Test]
    public async Task Compute_MatchesGitHubDocumentedVector()
    {
        var signature = GitHubWebhookSignature.Compute(Secret, Encoding.UTF8.GetBytes(Body));
        await Assert.That(signature).IsEqualTo(Expected);
    }

    [Test]
    public async Task Verify_AcceptsValidSignature()
    {
        var ok = GitHubWebhookSignature.Verify(Secret, Encoding.UTF8.GetBytes(Body), Expected);
        await Assert.That(ok).IsTrue();
    }

    [Test]
    public async Task Verify_RejectsTamperedBody()
    {
        var ok = GitHubWebhookSignature.Verify(Secret, Encoding.UTF8.GetBytes("Goodbye, World!"), Expected);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task Verify_RejectsWrongSecret()
    {
        var ok = GitHubWebhookSignature.Verify("not the secret", Encoding.UTF8.GetBytes(Body), Expected);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("sha256=deadbeef")]
    [Arguments("garbage")]
    public async Task Verify_RejectsMissingOrMalformedHeader(string? header)
    {
        var ok = GitHubWebhookSignature.Verify(Secret, Encoding.UTF8.GetBytes(Body), header);
        await Assert.That(ok).IsFalse();
    }
}
