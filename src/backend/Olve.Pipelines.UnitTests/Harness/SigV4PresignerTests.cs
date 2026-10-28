using Olve.Pipelines.Configuration;
using Olve.Pipelines.Shared.Storage;

namespace Olve.Pipelines.UnitTests.Harness;

public class SigV4PresignerTests
{
    private static readonly DateTimeOffset Fixed = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri InCluster = new("http://olve-pipelines-v2-garage.apps-beta.svc.cluster.local:3900");
    private const string Bucket = "olve-pipelines-v2-beta";
    private const string Key = "runs/abc/412/build/1/files/chart dir/a+b.tar";

    [Test]
    public async Task AwsPublishedExample_MatchesDocumentedSignature()
    {
        // AWS S3 docs, "Authenticating Requests: Using Query Parameters (AWS Signature Version 4)".
        var url = SigV4Presigner.Presign(
            "GET", "https", "examplebucket.s3.amazonaws.com", "/test.txt",
            new S3Credentials("AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"),
            "us-east-1", new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero), TimeSpan.FromSeconds(86400));

        await Assert.That(url).IsEqualTo(
            "https://examplebucket.s3.amazonaws.com/test.txt" +
            "?X-Amz-Algorithm=AWS4-HMAC-SHA256" +
            "&X-Amz-Credential=AKIAIOSFODNN7EXAMPLE%2F20130524%2Fus-east-1%2Fs3%2Faws4_request" +
            "&X-Amz-Date=20130524T000000Z&X-Amz-Expires=86400&X-Amz-SignedHeaders=host" +
            "&X-Amz-Signature=aeeed9bbccd4d02ee5c0109b86d86835f995330da4c265957d157751f604d404");
    }

    // Expected URLs generated with botocore 1.34 (s3v4, path-style, frozen clock) — an independent implementation.
    [Test]
    [Arguments("GET", "373f3cfbb7c206989d8581e81e1d5807fef6e279da88d88b2ed785593f14a85f")]
    [Arguments("PUT", "9e242dfdb914dc27d51f80f532939565f6d5a7e7d8721779b4673cc370bb0672")]
    public async Task PathStyleWithPort_MatchesBotocore(string method, string signature)
    {
        var url = SigV4Presigner.PresignPathStyle(method, InCluster, Bucket, Key,
            new S3Credentials("GKexample123", "secretexample456"), "us-east-1", Fixed, TimeSpan.FromSeconds(900));

        await Assert.That(url).IsEqualTo(
            "http://olve-pipelines-v2-garage.apps-beta.svc.cluster.local:3900/olve-pipelines-v2-beta/runs/abc/412/build/1/files/chart%20dir/a%2Bb.tar" +
            "?X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Credential=GKexample123%2F20261021%2Fus-east-1%2Fs3%2Faws4_request" +
            "&X-Amz-Date=20261021T120000Z&X-Amz-Expires=900&X-Amz-SignedHeaders=host" +
            $"&X-Amz-Signature={signature}");
    }

    [Test]
    [Arguments("GET", "d74a76169c8bc49ef888ac4b1041654cbaf98fb80f789d8b0f9db6892a237cb5")]
    [Arguments("PUT", "7f2bc44be51441772fab7229464548e591d6ccab56028c93e586988a217af7cc")]
    public async Task SessionToken_IsSigned_MatchesBotocore(string method, string signature)
    {
        var url = SigV4Presigner.PresignPathStyle(method, InCluster, Bucket, Key,
            new S3Credentials("GKexample123", "secretexample456", "FwoGZXIvYXdzEXAMPLE+/="), "us-east-1", Fixed, TimeSpan.FromSeconds(900));

        await Assert.That(url).Contains("&X-Amz-Security-Token=FwoGZXIvYXdzEXAMPLE%2B%2F%3D&");
        await Assert.That(url).EndsWith($"&X-Amz-Signature={signature}");
    }

    [Test]
    public async Task Host_IncludesNonDefaultPortOnly()
    {
        await Assert.That(SigV4Presigner.HostHeader(new Uri("http://garage:3900"))).IsEqualTo("garage:3900");
        await Assert.That(SigV4Presigner.HostHeader(new Uri("http://garage"))).IsEqualTo("garage");
        await Assert.That(SigV4Presigner.HostHeader(new Uri("https://s3.example.com:443"))).IsEqualTo("s3.example.com");
    }

    [Test]
    public async Task DifferentHost_ChangesSignature()
    {
        var creds = new S3Credentials("GKexample123", "secretexample456");
        var shortHost = SigV4Presigner.PresignPathStyle("GET", new Uri("http://olve-pipelines-v2-garage.apps-beta:3900"), Bucket, Key, creds, "us-east-1", Fixed, TimeSpan.FromSeconds(900));
        var fqdn = SigV4Presigner.PresignPathStyle("GET", InCluster, Bucket, Key, creds, "us-east-1", Fixed, TimeSpan.FromSeconds(900));

        await Assert.That(Signature(shortHost)).IsNotEqualTo(Signature(fqdn));
    }

    [Test]
    public async Task Expiry_IsClampedToSevenDays()
    {
        var url = SigV4Presigner.PresignPathStyle("GET", InCluster, Bucket, "k",
            new S3Credentials("a", "b"), "us-east-1", Fixed, TimeSpan.FromDays(30));

        await Assert.That(url).Contains("X-Amz-Expires=604800&");
    }

    [Test]
    public async Task Presigner_UsesInClusterEndpointAndClock()
    {
        var time = new SettableTimeProvider(Fixed);
        var presigner = new S3UrlPresigner(
            new PresignOptions(InCluster, Bucket, "us-east-1", TimeSpan.FromSeconds(900)),
            new DirectCredentialsProvider<S3Credentials>(new S3Credentials("GKexample123", "secretexample456")),
            time);

        var first = await presigner.PresignGetAsync(Key);
        var again = await presigner.PresignGetAsync(Key);
        time.Advance(TimeSpan.FromSeconds(1));
        var later = await presigner.PresignGetAsync(Key);

        await Assert.That(first).EndsWith("373f3cfbb7c206989d8581e81e1d5807fef6e279da88d88b2ed785593f14a85f");
        await Assert.That(again).IsEqualTo(first);
        await Assert.That(later).IsNotEqualTo(first);
    }

    private static string Signature(string url) => url[(url.LastIndexOf('=') + 1)..];

    private sealed class SettableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
