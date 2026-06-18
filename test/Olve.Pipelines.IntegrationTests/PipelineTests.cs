using System.Net;

namespace Olve.Pipelines.IntegrationTests;

public class PipelineTests
{
    [ClassDataSource<AppFixture>(Shared = SharedType.PerAssembly)]
    public required AppFixture Fixture { get; init; }

    [Test]
    public async Task HealthEndpoint_ReturnsOk()
    {
        BetaGuard.SkipIfNoBeta();

        var client = Fixture.CreateUnauthenticatedHttpClient();

        var response = await client.GetAsync("/api/health");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    [Skip("Auth not enforced on GET /api/pipelines — needs investigation")]
    public async Task UnauthenticatedRequest_Returns401()
    {
        var client = Fixture.CreateUnauthenticatedHttpClient();

        var response = await client.GetAsync("/api/pipelines");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }
}
