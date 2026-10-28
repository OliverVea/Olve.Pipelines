using System.Text;
using Olve.Pipelines.Sources;

namespace Olve.Pipelines.UnitTests.Sources;

public class WebhookVerificationTests
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"ref":"refs/heads/main"}""");

    private static WebhookDelivery Delivery(params (string Name, string Value)[] headers) =>
        new(Body, headers.ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase));

    [Test]
    public async Task HmacSha256_AcceptsItsOwnSignature_InAnyHeaderCase()
    {
        var hmac = new WebhookVerification.HmacSha256("s3cret", "X-Signature", "sha256=");

        await Assert.That(hmac.Verify(Delivery(("x-signature", hmac.Sign(Body))))).IsTrue();
    }

    [Test]
    public async Task HmacSha256_RejectsWrongSecret_MissingHeader_AndMissingPrefix()
    {
        var hmac = new WebhookVerification.HmacSha256("s3cret", "X-Signature", "sha256=");
        var other = new WebhookVerification.HmacSha256("other", "X-Signature", "sha256=");

        await Assert.That(hmac.Verify(Delivery(("X-Signature", other.Sign(Body))))).IsFalse();
        await Assert.That(hmac.Verify(Delivery())).IsFalse();
        await Assert.That(hmac.Verify(Delivery(("X-Signature", hmac.Sign(Body)["sha256=".Length..])))).IsFalse();
    }

    [Test]
    public async Task Bearer_AcceptsOnlyTheToken()
    {
        var bearer = new WebhookVerification.Bearer("tok");

        await Assert.That(bearer.Verify(Delivery(("Authorization", "Bearer tok")))).IsTrue();
        await Assert.That(bearer.Verify(Delivery(("Authorization", "Bearer other")))).IsFalse();
        await Assert.That(bearer.Verify(Delivery())).IsFalse();
    }
}
