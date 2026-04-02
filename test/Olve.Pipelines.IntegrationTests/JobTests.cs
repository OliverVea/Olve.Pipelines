using System.Net;

namespace Olve.Pipelines.IntegrationTests;

public class JobTests
{
    [ClassDataSource<AppFixture>(Shared = SharedType.PerAssembly)]
    public required AppFixture Fixture { get; init; }

    [Test]
    public async Task ListJobs_ReturnsOk()
    {
        var client = Fixture.CreateApiClient();

        var response = await client.JobsAll();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content).IsNotNull();
    }

    [Test]
    public async Task GetJob_NotFound_ReturnsUnprocessableEntity()
    {
        var client = Fixture.CreateApiClient();

        var response = await client.JobsGET(Guid.NewGuid().ToString());

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
    }

    [Test]
    public async Task GetQueue_ReturnsOk()
    {
        var client = Fixture.CreateApiClient();

        var response = await client.Queue();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content).IsNotNull();
    }

    [Test]
    public async Task DeleteJob_NotFound_ReturnsNotFound()
    {
        var client = Fixture.CreateApiClient();

        var response = await client.JobsDELETE(Guid.NewGuid().ToString());

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task CancelJob_NotFound_ReturnsUnprocessableEntity()
    {
        var client = Fixture.CreateApiClient();

        var response = await client.Cancel(Guid.NewGuid().ToString());

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
    }
}
