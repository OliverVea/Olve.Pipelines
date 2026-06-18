using System.Net;

namespace Olve.Pipelines.IntegrationTests;

public class JobTests
{
    [ClassDataSource<AppFixture>(Shared = SharedType.PerAssembly)]
    public required AppFixture Fixture { get; init; }

    [Test]
    public async Task ListJobs_ReturnsOk()
    {
        BetaGuard.SkipIfNoBeta();

        var client = Fixture.CreateApiClient();

        var response = await client.ListJobs();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content).IsNotNull();
    }

    [Test]
    public async Task GetJob_NotFound_ReturnsUnprocessableEntity()
    {
        BetaGuard.SkipIfNoBeta();

        var client = Fixture.CreateApiClient();

        var response = await client.GetJob(Guid.NewGuid().ToString());

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task GetQueue_ReturnsOk()
    {
        BetaGuard.SkipIfNoBeta();

        var client = Fixture.CreateApiClient();

        var response = await client.GetJobQueue();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content).IsNotNull();
    }

    [Test]
    public async Task DeleteJob_NotFound_ReturnsNotFound()
    {
        BetaGuard.SkipIfNoBeta();

        var client = Fixture.CreateApiClient();

        var response = await client.DeleteJob(Guid.NewGuid().ToString());

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task CancelJob_NotFound_ReturnsUnprocessableEntity()
    {
        BetaGuard.SkipIfNoBeta();

        var client = Fixture.CreateApiClient();

        var response = await client.CancelJob(Guid.NewGuid().ToString());

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }
}
