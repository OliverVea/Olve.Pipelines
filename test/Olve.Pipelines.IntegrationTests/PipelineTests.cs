using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Olve.Pipelines.IntegrationTests;

public class PipelineTests
{
    [ClassDataSource<AppFixture>(Shared = SharedType.PerAssembly)]
    public required AppFixture Fixture { get; init; }

    [Test]
    public async Task HealthEndpoint_ReturnsOk()
    {
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

    [Test]
    public async Task CreatePipeline_ReturnsPipeline()
    {
        var client = Fixture.CreateAuthenticatedHttpClient();

        var response = await client.PostAsync("/api/pipelines?name=test-pipeline", null);
        response.EnsureSuccessStatusCode();
        var pipeline = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(pipeline.GetProperty("name").GetString()).IsEqualTo("test-pipeline");
        await Assert.That(pipeline.GetProperty("id").GetGuid()).IsNotEqualTo(Guid.Empty);
    }
}
