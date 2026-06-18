using System.Security.Cryptography;
using System.Text;

namespace Olve.Pipelines.Pipelines.Triggers;

/// <summary>
/// Verifies the <c>X-Hub-Signature-256</c> header GitHub sends with each webhook delivery: an
/// HMAC-SHA256 of the raw request body keyed by the hook secret, formatted <c>sha256=&lt;hex&gt;</c>.
/// We reuse the trigger's own <see cref="Trigger.Secret"/> as that key (it is also the secret we
/// register on the hook), so no separate inbound secret is stored.
/// </summary>
public static class GitHubWebhookSignature
{
    public const string HeaderName = "X-Hub-Signature-256";

    /// <summary>Computes the <c>sha256=&lt;hex&gt;</c> signature for <paramref name="body"/>.</summary>
    public static string Compute(string secret, ReadOnlySpan<byte> body)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return "sha256=" + Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Constant-time comparison of the delivered signature header against the expected value.
    /// Returns false for a missing/malformed header rather than throwing.
    /// </summary>
    public static bool Verify(string secret, ReadOnlySpan<byte> body, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(signatureHeader))
            return false;

        var expected = Encoding.UTF8.GetBytes(Compute(secret, body));
        var actual = Encoding.UTF8.GetBytes(signatureHeader);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
