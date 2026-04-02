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

        var pipeline = await client.CreatePipeline($"prod-step-test-{Guid.NewGuid():N}");
        var response = await client.CreateProductionStep(pipeline.Content!.Id.ToString(), new CreateProductionStepRequest { Name = "test-step" });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content!.Name).IsEqualTo("test-step");
    }

    [Test]
    public async Task CreateProductionStep_ForNonExistentPipeline_Returns422()
    {
        var client = Fixture.CreateApiClient();

        var response = await client.CreateProductionStep(Guid.NewGuid().ToString(), new CreateProductionStepRequest { Name = "orphan" });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task ListProductionSteps_ForExistingPipeline_ReturnsOk()
    {
        var client = Fixture.CreateApiClient();

        var pipeline = await client.CreatePipeline($"list-prod-test-{Guid.NewGuid():N}");
        var pipelineId = pipeline.Content!.Id.ToString();
        await client.CreateProductionStep(pipelineId, new CreateProductionStepRequest { Name = "step-1" });
        await client.CreateProductionStep(pipelineId, new CreateProductionStepRequest { Name = "step-2" });

        var response = await client.ListProductionSteps(pipelineId);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content!.Count).IsEqualTo(2);
    }

    [Test]
    public async Task DeletePipeline_CascadeDeletesProductionSteps()
    {
        var client = Fixture.CreateApiClient();

        var pipeline = await client.CreatePipeline($"cascade-prod-test-{Guid.NewGuid():N}");
        var pipelineId = pipeline.Content!.Id.ToString();
        var step = await client.CreateProductionStep(pipelineId, new CreateProductionStepRequest { Name = "doomed-step" });
        var stepId = step.Content!.Id.ToString();

        await client.DeletePipeline(pipelineId);

        var response = await client.GetProductionStep(stepId);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task SetConfiguration_ReturnsOk()
    {
        var client = Fixture.CreateApiClient();

        var pipeline = await client.CreatePipeline($"config-test-{Guid.NewGuid():N}");
        var step = await client.CreateProductionStep(pipeline.Content!.Id.ToString(), new CreateProductionStepRequest { Name = "config-step" });
        var stepId = step.Content!.Id.ToString();

        var response = await client.SetProductionStepConfiguration(stepId, new SetStepConfigurationRequest
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

        var pipeline = await client.CreatePipeline($"no-config-test-{Guid.NewGuid():N}");
        var step = await client.CreateProductionStep(pipeline.Content!.Id.ToString(), new CreateProductionStepRequest { Name = "bare-step" });

        var response = await client.GetProductionStepConfiguration(step.Content!.Id.ToString());
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task TriggerProduction_WithConfiguredSteps_ReturnsOk()
    {
        var client = Fixture.CreateApiClient();

        var pipeline = await client.CreatePipeline($"trigger-test-{Guid.NewGuid():N}");
        var pipelineId = pipeline.Content!.Id.ToString();
        var step = await client.CreateProductionStep(pipelineId, new CreateProductionStepRequest { Name = "build" });
        await client.SetProductionStepConfiguration(step.Content!.Id.ToString(), new SetStepConfigurationRequest
        {
            Image = "alpine:latest",
            Script = "echo build",
        });

        var response = await client.TriggerProduction(pipelineId);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task TriggerProduction_WithNoSteps_Returns422()
    {
        var client = Fixture.CreateApiClient();

        var pipeline = await client.CreatePipeline($"trigger-no-steps-{Guid.NewGuid():N}");

        var response = await client.TriggerProduction(pipeline.Content!.Id.ToString());

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task TriggerProduction_WithUnconfiguredStep_Returns422()
    {
        var client = Fixture.CreateApiClient();

        var pipeline = await client.CreatePipeline($"trigger-unconfig-{Guid.NewGuid():N}");
        var pipelineId = pipeline.Content!.Id.ToString();
        await client.CreateProductionStep(pipelineId, new CreateProductionStepRequest { Name = "unconfigured" });

        var response = await client.TriggerProduction(pipelineId);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task TriggerProduction_ForNonExistentPipeline_Returns422()
    {
        var client = Fixture.CreateApiClient();

        var response = await client.TriggerProduction(Guid.NewGuid().ToString());

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }
}
