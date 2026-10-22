using System.Net;

namespace Olve.Pipelines.IntegrationTests;

public class AttemptTests
{
    [ClassDataSource<AppFixture>(Shared = SharedType.PerAssembly)]
    public required AppFixture Fixture { get; init; }

    [Test]
    public async Task ListActiveAttempts_ReturnsOk()
    {
        BetaGuard.SkipIfNoBeta();

        var client = Fixture.CreateApiClient();

        var response = await client.ListActiveAttempts("active");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content).IsNotNull();
    }

    [Test]
    public async Task CancelStep_UnknownRun_ReturnsNotFound()
    {
        BetaGuard.SkipIfNoBeta();

        var client = Fixture.CreateApiClient();

        var response = await client.CancelStep(Guid.NewGuid().ToString(), 1, "build");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task ListRuns_UnknownPipeline_ReturnsOk()
    {
        BetaGuard.SkipIfNoBeta();

        var client = Fixture.CreateApiClient();

        var response = await client.ListRuns(Guid.NewGuid().ToString());

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
}
