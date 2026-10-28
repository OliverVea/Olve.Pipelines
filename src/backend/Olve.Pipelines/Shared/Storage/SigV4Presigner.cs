using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Olve.Pipelines.Configuration;

namespace Olve.Pipelines.Shared.Storage;

/// <summary>
/// AWS Signature Version 4 query-string pre-signing for S3 (AOT-safe, BCL crypto only). Pure: the
/// signing instant is passed in, so a URL is a deterministic function of its inputs.
/// <para>
/// Signs <c>host</c> only, with an <c>UNSIGNED-PAYLOAD</c> body hash — the harness and olve-init send
/// a bare GET/PUT with no extra headers. <c>Host</c> is part of the signature, so the URL works only
/// against the exact <c>host[:port]</c> it was signed for (spec: pre-sign against the in-cluster host).
/// </para>
/// </summary>
public static class SigV4Presigner
{
    /// <summary>SigV4 caps a pre-signed URL's validity at seven days.</summary>
    public static readonly TimeSpan MaxExpiry = TimeSpan.FromDays(7);

    private const string Algorithm = "AWS4-HMAC-SHA256";
    private const string Service = "s3";

    /// <summary>
    /// Pre-signs a path-style object URL: <c>&lt;endpoint&gt;/&lt;bucket&gt;/&lt;key&gt;</c>. The endpoint's own
    /// path (if any) is kept as a prefix. Expiry is clamped to [1 s, 7 days].
    /// </summary>
    public static string PresignPathStyle(
        string method, Uri endpoint, string bucket, string key,
        S3Credentials credentials, string region, DateTimeOffset now, TimeSpan expiry)
    {
        var basePath = endpoint.AbsolutePath.TrimEnd('/');
        var canonicalUri = $"{basePath}/{EncodePath(bucket)}/{EncodePath(key)}";
        return Presign(method, endpoint.Scheme, HostHeader(endpoint), canonicalUri, credentials, region, now, expiry);
    }

    /// <summary>
    /// Pre-signs <c>&lt;scheme&gt;://&lt;host&gt;&lt;canonicalUri&gt;</c>. <paramref name="canonicalUri"/> must already be
    /// URI-encoded (S3 encodes each segment once and keeps <c>/</c>); <paramref name="host"/> is the exact
    /// <c>Host</c> header value, including a non-default port.
    /// </summary>
    public static string Presign(
        string method, string scheme, string host, string canonicalUri,
        S3Credentials credentials, string region, DateTimeOffset now, TimeSpan expiry)
    {
        var expires = (long)Math.Clamp(expiry.TotalSeconds, 1, MaxExpiry.TotalSeconds);
        var utc = now.UtcDateTime;
        var amzDate = utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var scope = $"{date}/{region}/{Service}/aws4_request";

        var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Amz-Algorithm"] = Algorithm,
            ["X-Amz-Credential"] = $"{credentials.AccessKey}/{scope}",
            ["X-Amz-Date"] = amzDate,
            ["X-Amz-Expires"] = expires.ToString(CultureInfo.InvariantCulture),
            ["X-Amz-SignedHeaders"] = "host",
        };
        if (!string.IsNullOrEmpty(credentials.SessionToken))
            query["X-Amz-Security-Token"] = credentials.SessionToken;

        var canonicalQuery = string.Join('&', query.Select(kv => $"{Encode(kv.Key)}={Encode(kv.Value)}"));

        var canonicalRequest = string.Join('\n',
            method.ToUpperInvariant(),
            canonicalUri,
            canonicalQuery,
            $"host:{host}\n",
            "host",
            "UNSIGNED-PAYLOAD");

        var stringToSign = string.Join('\n',
            Algorithm,
            amzDate,
            scope,
            Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        var signingKey = Hmac(Encoding.UTF8.GetBytes("AWS4" + credentials.SecretKey), date);
        signingKey = Hmac(signingKey, region);
        signingKey = Hmac(signingKey, Service);
        signingKey = Hmac(signingKey, "aws4_request");
        var signature = Hex(Hmac(signingKey, stringToSign));

        return $"{scheme}://{host}{canonicalUri}?{canonicalQuery}&X-Amz-Signature={signature}";
    }

    /// <summary>The <c>Host</c> header a client sends for <paramref name="endpoint"/>: the port only when non-default.</summary>
    public static string HostHeader(Uri endpoint) =>
        endpoint.IsDefaultPort ? endpoint.IdnHost : $"{endpoint.IdnHost}:{endpoint.Port.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>RFC 3986 percent-encoding (unreserved kept, uppercase hex), as SigV4 requires.</summary>
    public static string Encode(string value) => EncodeCore(value, keepSlash: false);

    /// <summary>Like <see cref="Encode"/> but keeps <c>/</c> (an S3 object key in a path).</summary>
    public static string EncodePath(string value) => EncodeCore(value, keepSlash: true);

    private static string EncodeCore(string value, bool keepSlash)
    {
        var sb = new StringBuilder(value.Length * 2);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.' or '~'
                || (keepSlash && c == '/'))
                sb.Append(c);
            else
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
