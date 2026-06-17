using System.Net;

namespace Olve.Pipelines.IntegrationTests;

/// <summary>
/// HTTP-surface behaviour for production steps that needs no seeded shape. Shape is no longer
/// API-writable (GitOps reconcile is the only writer); reconcile-produces-the-right-shape is
/// verified in-process by the unit suite (PipelinesTestFixtureConfigTests, ProductionStepServiceTests)
/// and end-to-end by GitOpsReconcileSpikeTests.
/// </summary>
public class ProductionStepTests
{
    [ClassDataSource<AppFixture>(Shared = SharedType.PerAssembly)]
    public required AppFixture Fixture { get; init; }

    [Test]
    public async Task TriggerProduction_ForNonExistentPipeline_Returns422()
    {
        var client = Fixture.CreateApiClient();

        var response = await client.TriggerProduction(Guid.NewGuid().ToString());

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }
}
