using System.Net;
using Olve.Pipelines.Client;

namespace Olve.Pipelines.IntegrationTests;

public class ProductionStepTests
{
    [ClassDataSource<AppFixture>(Shared = SharedType.PerAssembly)]
    public required AppFixture Fixture { get; init; }

    [Test]
    public async Task CreateProductionStep_ForExistingPipeline_ReturnsOk()
    {
        var client = Fixture.CreateApiClient();

        var pipeline = await client.PipelinesPOST($"prod-step-test-{Guid.NewGuid():N}");
        var response = await client.ProductionPOST(pipeline.Content!.Id.ToString(), new CreateProductionStepRequest { Name = "test-step" });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content!.Name).IsEqualTo("test-step");
    }

    [Test]
    public async Task CreateProductionStep_ForNonExistentPipeline_Returns422()
    {
        var client = Fixture.CreateApiClient();

        var response = await client.ProductionPOST(Guid.NewGuid().ToString(), new CreateProductionStepRequest { Name = "orphan" });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
    }

    [Test]
    public async Task ListProductionSteps_ForExistingPipeline_ReturnsOk()
    {
        var client = Fixture.CreateApiClient();

        var pipeline = await client.PipelinesPOST($"list-prod-test-{Guid.NewGuid():N}");
        var pipelineId = pipeline.Content!.Id.ToString();
        await client.ProductionPOST(pipelineId, new CreateProductionStepRequest { Name = "step-1" });
        await client.ProductionPOST(pipelineId, new CreateProductionStepRequest { Name = "step-2" });

        var response = await client.ProductionAll(pipelineId);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content!.Count).IsEqualTo(2);
    }

    [Test]
    public async Task DeletePipeline_CascadeDeletesProductionSteps()
    {
        var client = Fixture.CreateApiClient();

        var pipeline = await client.PipelinesPOST($"cascade-prod-test-{Guid.NewGuid():N}");
        var pipelineId = pipeline.Content!.Id.ToString();
        var step = await client.ProductionPOST(pipelineId, new CreateProductionStepRequest { Name = "doomed-step" });
        var stepId = step.Content!.Id.ToString();

        await client.PipelinesDELETE(pipelineId);

        var response = await client.ProductionGET(stepId);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
    }

    [Test]
    public async Task SetConfiguration_ReturnsOk()
    {
        var client = Fixture.CreateApiClient();

        var pipeline = await client.PipelinesPOST($"config-test-{Guid.NewGuid():N}");
        var step = await client.ProductionPOST(pipeline.Content!.Id.ToString(), new CreateProductionStepRequest { Name = "config-step" });
        var stepId = step.Content!.Id.ToString();

        var response = await client.ConfigurationPUT(stepId, new SetStepConfigurationRequest
        {
            Image = "alpine:latest",
            Script = "echo hello",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content!.Image).IsEqualTo("alpine:latest");
        await Assert.That(response.Content!.Script).IsEqualTo("echo hello");
    }

    [Test]
    public async Task GetConfiguration_WhenNotSet_Returns422()
    {
        var client = Fixture.CreateApiClient();

        var pipeline = await client.PipelinesPOST($"no-config-test-{Guid.NewGuid():N}");
        var step = await client.ProductionPOST(pipeline.Content!.Id.ToString(), new CreateProductionStepRequest { Name = "bare-step" });

        var response = await client.ConfigurationGET(step.Content!.Id.ToString());
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
    }
}
