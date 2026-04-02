using System.Net;
using Olve.Pipelines.Client;

namespace Olve.Pipelines.IntegrationTests;

public class PipelineSourceTests
{
    [ClassDataSource<AppFixture>(Shared = SharedType.PerAssembly)]
    public required AppFixture Fixture { get; init; }

    [Test]
    public async Task CreateSource_ForExistingPipeline_ReturnsOk()
    {
        var client = Fixture.CreateApiClient();

        var pipeline = await client.PipelinesPOST($"source-test-{Guid.NewGuid():N}");
        var response = await client.SourcesPOST(pipeline.Content!.Id.ToString(), new CreatePipelineSourceRequest { Name = "test-source" });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content!.Name).IsEqualTo("test-source");
    }

    [Test]
    public async Task CreateSource_ForNonExistentPipeline_Returns422()
    {
        var client = Fixture.CreateApiClient();

        var response = await client.SourcesPOST(Guid.NewGuid().ToString(), new CreatePipelineSourceRequest { Name = "orphan" });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
    }

    [Test]
    public async Task ListSources_ForExistingPipeline_ReturnsOk()
    {
        var client = Fixture.CreateApiClient();

        var pipeline = await client.PipelinesPOST($"list-source-test-{Guid.NewGuid():N}");
        var pipelineId = pipeline.Content!.Id.ToString();
        await client.SourcesPOST(pipelineId, new CreatePipelineSourceRequest { Name = "src-1" });
        await client.SourcesPOST(pipelineId, new CreatePipelineSourceRequest { Name = "src-2" });

        var response = await client.SourcesAll(pipelineId);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content!.Count).IsEqualTo(2);
    }

    [Test]
    public async Task DeletePipeline_CascadeDeletesSources()
    {
        var client = Fixture.CreateApiClient();

        var pipeline = await client.PipelinesPOST($"cascade-test-{Guid.NewGuid():N}");
        var pipelineId = pipeline.Content!.Id.ToString();
        var source = await client.SourcesPOST(pipelineId, new CreatePipelineSourceRequest { Name = "doomed-source" });
        var sourceId = source.Content!.Id.ToString();

        await client.PipelinesDELETE(pipelineId);

        var response = await client.SourcesGET(sourceId);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
    }
}
