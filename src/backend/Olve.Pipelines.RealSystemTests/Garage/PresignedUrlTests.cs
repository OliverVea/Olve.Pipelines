using System.Net;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Shared.Storage;

namespace Olve.Pipelines.RealSystemTests.Garage;

/// <summary>Step pods only ever hold pre-signed URLs, and Garage is the one that checks them.</summary>
public class PresignedUrlTests
{
    private static readonly HttpClient Http = new();

    private static S3UrlPresigner Presigner(TestServer server, TimeProvider? time = null)
    {
        var target = server.Bucket.Target;
        return new S3UrlPresigner(
            new PresignOptions(new Uri(target.Endpoint), target.Bucket, "us-east-1", TimeSpan.FromMinutes(5)),
            new DirectCredentialsProvider<S3Credentials>(new S3Credentials(target.AccessKey, target.SecretKey)),
            time ?? TimeProvider.System);
    }

    private sealed class PastClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddHours(-2);
    }

    [Test]
    public async Task PresignedPut_ThenGet_RoundTripsTheBytes()
    {
        await using var server = await TestServer.LeaseAsync();
        var presigner = Presigner(server);
        var key = $"{TestServer.NewName("url")}/result file+1.tar.gz";
        byte[] body = [0, 1, 2, 255];

        using var put = await Http.PutAsync(await presigner.PresignPutAsync(key), new ByteArrayContent(body));
        var got = await Http.GetByteArrayAsync(await presigner.PresignGetAsync(key));

        await Assert.That(put.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(got).IsEquivalentTo(body);
    }

    [Test]
    public async Task PresignedGet_OfAMissingKey_Is404()
    {
        await using var server = await TestServer.LeaseAsync();

        using var response = await Http.GetAsync(await Presigner(server).PresignGetAsync(TestServer.NewName("url")));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task ExpiredUrl_IsRefused()
    {
        await using var server = await TestServer.LeaseAsync();
        var key = TestServer.NewName("url");
        await server.Bucket.PutAsync(key, "x");

        using var response = await Http.GetAsync(await Presigner(server, new PastClock()).PresignGetAsync(key, TimeSpan.FromMinutes(1)));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await response.Content.ReadAsStringAsync()).Contains("Date is too old");
    }

    // A long step PUTs its result with a URL signed when the step started.
    [Test]
    public async Task OldUrl_WithinItsExpiry_StillWorks()
    {
        await using var server = await TestServer.LeaseAsync();
        var key = TestServer.NewName("url");

        using var put = await Http.PutAsync(
            await Presigner(server, new PastClock()).PresignPutAsync(key, TimeSpan.FromHours(3)), new StringContent("late result"));

        await Assert.That(put.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await Http.GetStringAsync(await Presigner(server).PresignGetAsync(key))).IsEqualTo("late result");
    }

    [Test]
    public async Task UrlForOneKey_DoesNotOpenAnother()
    {
        await using var server = await TestServer.LeaseAsync();
        var root = TestServer.NewName("url");
        await server.Bucket.PutAsync($"{root}/other", "x");
        var url = await Presigner(server).PresignGetAsync($"{root}/mine");

        using var response = await Http.GetAsync(url.Replace($"{root}/mine", $"{root}/other", StringComparison.Ordinal));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }
}
