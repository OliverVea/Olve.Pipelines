using Olve.Pipelines.Sources;

namespace Olve.Pipelines.GitHub;

/// <summary>
/// GitHub's delivery signature: <c>X-Hub-Signature-256: sha256=&lt;hex&gt;</c>, an HMAC-SHA256 of the raw
/// body keyed by the hook secret (a <see cref="WebhookVerification.HmacSha256"/> preset).
/// </summary>
public static class GitHubWebhookSignature
{
    public const string HeaderName = "X-Hub-Signature-256";

    public static WebhookVerification.HmacSha256 For(string secret) => new(secret, HeaderName, "sha256=");

    /// <summary>Computes the <c>sha256=&lt;hex&gt;</c> signature for <paramref name="body"/>.</summary>
    public static string Compute(string secret, ReadOnlySpan<byte> body) => For(secret).Sign(body);

    /// <summary>Constant-time check of a delivered signature header; false when it is missing or malformed.</summary>
    public static bool Verify(string secret, ReadOnlySpan<byte> body, string? signatureHeader) => For(secret).Verify(body, signatureHeader);
}
