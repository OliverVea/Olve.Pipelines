using System.Security.Cryptography;
using System.Text;

namespace Olve.Pipelines.Sources;

/// <summary>One inbound webhook request: the raw body (signatures cover the exact bytes) and its headers.</summary>
public sealed record WebhookDelivery(byte[] Body, IReadOnlyDictionary<string, string> Headers)
{
    public const int MaxBodyBytes = 5 * 1024 * 1024;

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    /// <summary>Reads the raw body once, refusing bodies over <see cref="MaxBodyBytes"/>.</summary>
    public static async Task<Result<WebhookDelivery>> ReadAsync(Stream body, IHeaderDictionary headers, CancellationToken ct)
    {
        if (headers.ContentLength > MaxBodyBytes)
            return new ResultProblem("Webhook body exceeds {0} bytes.", MaxBodyBytes);

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
                return new ResultProblem("Webhook body exceeds {0} bytes.", MaxBodyBytes);
            buffer.Write(chunk, 0, read);
        }

        return new WebhookDelivery(buffer.ToArray(),
            headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase));
    }
}

/// <summary>How a webhook source authenticates its deliveries. Comparisons are constant-time.</summary>
public abstract record WebhookVerification
{
    public abstract bool Verify(WebhookDelivery delivery);

    /// <summary>HMAC-SHA256 of the body keyed by <see cref="Secret"/>, hex, in <see cref="Header"/> after <see cref="Prefix"/>.</summary>
    public sealed record HmacSha256(string Secret, string Header, string Prefix) : WebhookVerification
    {
        public string Sign(ReadOnlySpan<byte> body) =>
            Prefix + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), body));

        public override bool Verify(WebhookDelivery delivery) => Verify(delivery.Body, delivery.Header(Header));

        public bool Verify(ReadOnlySpan<byte> body, string? signature) =>
            !string.IsNullOrEmpty(signature)
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Sign(body)), Encoding.UTF8.GetBytes(signature));
    }

    /// <summary><c>Authorization: Bearer &lt;Token&gt;</c>.</summary>
    public sealed record Bearer(string Token) : WebhookVerification
    {
        public override bool Verify(WebhookDelivery delivery) =>
            delivery.Header("Authorization") is { } auth
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes("Bearer " + Token), Encoding.UTF8.GetBytes(auth));
    }
}
