using System.Net;

namespace Olve.Pipelines.IntegrationTests;

/// <summary>
/// Read-side export of a pipeline's shape as a <c>PipelineDocument</c> (<c>GET /document</c>).
/// The document-reflects-reconciled-shape assertion is covered in-process by the unit suite
/// (PipelinesTestFixtureConfigTests + PipelineDocumentBuilderTests); this keeps only the
/// HTTP-surface case that needs no seeded shape.
/// </summary>
public class PipelineDocumentTests
{
    [ClassDataSource<AppFixture>(Shared = SharedType.PerAssembly)]
    public required AppFixture Fixture { get; init; }

    [Test]
    public async Task GetPipelineDocument_UnknownPipeline_Returns4xx()
    {
        var client = Fixture.CreateAuthenticatedHttpClient();

        var response = await client.GetAsync($"/api/pipelines/{Guid.NewGuid()}/document");

        await Assert.That((int)response.StatusCode).IsBetween(400, 499);
    }
}
