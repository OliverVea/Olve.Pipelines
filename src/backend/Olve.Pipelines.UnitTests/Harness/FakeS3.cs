using System.Collections.Concurrent;
using System.Net;
using Amazon.Runtime;
using Amazon.S3;

namespace Olve.Pipelines.UnitTests.Harness;

/// <summary>
/// A real <see cref="AmazonS3Client"/> over an in-memory, path-style object store (GET, HEAD, list, PUT, DELETE), configured
/// like production (no aws-chunked uploads), so tests exercise the SDK calls the service actually makes.
/// </summary>
internal sealed class FakeS3 : HttpMessageHandler
{
    public const string Bucket = "test-bucket";

    public ConcurrentDictionary<string, byte[]> Objects { get; } = new();
    public ConcurrentDictionary<string, string> ContentTypes { get; } = new();
    public ConcurrentQueue<string> Puts { get; } = new();

    /// <summary>Keys per listing page, in S3's ordinal key order.</summary>
    public int ListPageSize { get; set; } = 1000;

    public IAmazonS3 Client() => new AmazonS3Client(new BasicAWSCredentials("test", "test"), new AmazonS3Config
    {
        ServiceURL = "http://s3.test",
        ForcePathStyle = true,
        MaxErrorRetry = 0,
        RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        HttpClientFactory = new Factory(this),
    });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
        if (request.Method == HttpMethod.Get && query["list-type"] == "2")
            return List(query["prefix"] ?? "", int.Parse(query["continuation-token"] ?? "0"));

        var path = Uri.UnescapeDataString(request.RequestUri.AbsolutePath).TrimStart('/');
        var key = path.StartsWith(Bucket + "/", StringComparison.Ordinal) ? path[(Bucket.Length + 1)..] : path;

        if (request.Method == HttpMethod.Put)
        {
            var put = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
            if (request.Headers.TryGetValues("x-amz-content-sha256", out var sha) && sha.First().StartsWith("STREAMING-", StringComparison.Ordinal))
                put = Dechunk(put);
            Objects[key] = put;
            Puts.Enqueue(key);
            return new HttpResponseMessage(HttpStatusCode.OK) { Headers = { ETag = ETag(put) } };
        }

        if (request.Method == HttpMethod.Delete)
        {
            Objects.TryRemove(key, out _);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        if (!Objects.TryGetValue(key, out var body))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(
                    $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>NoSuchKey</Code><Message>no such key</Message><Key>{key}</Key></Error>",
                    System.Text.Encoding.UTF8, "application/xml"),
            };
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        response.Headers.ETag = ETag(body);
        response.Content.Headers.LastModified = DateTimeOffset.UnixEpoch;
        if (ContentTypes.TryGetValue(key, out var contentType))
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return response;
    }

    private HttpResponseMessage List(string prefix, int start)
    {
        var keys = Objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        var next = start + ListPageSize;
        var contents = string.Concat(keys.Skip(start).Take(ListPageSize).Select(k =>
            $"<Contents><Key>{k}</Key><LastModified>1970-01-01T00:00:00.000Z</LastModified><Size>{Objects[k].Length}</Size></Contents>"));
        var truncated = next < keys.Count;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><ListBucketResult><Name>{Bucket}</Name><Prefix>{prefix}</Prefix>" +
                $"<IsTruncated>{(truncated ? "true" : "false")}</IsTruncated>{(truncated ? $"<NextContinuationToken>{next}</NextContinuationToken>" : "")}{contents}</ListBucketResult>",
                System.Text.Encoding.UTF8, "application/xml"),
        };
    }

    // SigV4 streaming upload (what the SDK sends over plain HTTP): "<hex size>;chunk-signature=…\r\n<data>\r\n", ending at size 0.
    private static byte[] Dechunk(byte[] body)
    {
        var data = new MemoryStream();
        var i = 0;
        while (true)
        {
            var lineEnd = Array.IndexOf(body, (byte)'\n', i);
            var header = System.Text.Encoding.ASCII.GetString(body, i, lineEnd - i).TrimEnd('\r');
            var size = Convert.ToInt32(header.Split(';')[0], 16);
            if (size == 0)
                return data.ToArray();
            data.Write(body, lineEnd + 1, size);
            i = lineEnd + 1 + size + 2;
        }
    }

    // The SDK checks the ETag against the body's MD5.
    private static System.Net.Http.Headers.EntityTagHeaderValue ETag(byte[] body) =>
        new($"\"{Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(body))}\"");

    private sealed class Factory(FakeS3 handler) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(handler, disposeHandler: false);
    }
}
