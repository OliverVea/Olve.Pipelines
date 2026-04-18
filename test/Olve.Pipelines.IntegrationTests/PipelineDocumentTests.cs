using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Olve.Pipelines.IntegrationTests;

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

    [Test]
    public async Task GetPipelineDocument_EmptyPipeline_ReturnsDocumentWithVersionAndName()
    {
        var client = Fixture.CreateAuthenticatedHttpClient();
        var name = $"doc-empty-{Guid.NewGuid():N}";
        var created = await client.PostAsync($"/api/pipelines?name={name}", null);
        created.EnsureSuccessStatusCode();
        var pipelineId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var response = await client.GetAsync($"/api/pipelines/{pipelineId}/document");
        response.EnsureSuccessStatusCode();

        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(doc.GetProperty("apiVersion").GetString()).IsEqualTo("0.0");
        await Assert.That(doc.GetProperty("name").GetString()).IsEqualTo(name);
        await Assert.That(doc.GetProperty("productionSteps").GetArrayLength()).IsEqualTo(0);
        await Assert.That(doc.GetProperty("processingSteps").GetArrayLength()).IsEqualTo(0);
        await Assert.That(doc.GetProperty("triggers").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task GetPipelineDocument_ProductionStepWithConfig_IsIncluded()
    {
        var client = Fixture.CreateAuthenticatedHttpClient();
        var name = $"doc-prod-{Guid.NewGuid():N}";
        var created = await client.PostAsync($"/api/pipelines?name={name}", null);
        created.EnsureSuccessStatusCode();
        var pipelineId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var stepResp = await client.PostAsJsonAsync(
            $"/api/pipelines/{pipelineId}/production",
            new { Name = "build" });
        stepResp.EnsureSuccessStatusCode();
        var stepId = (await stepResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var configResp = await client.PutAsJsonAsync(
            $"/api/production-steps/{stepId}/configuration",
            new { Image = "alpine:latest", Script = "echo build" });
        configResp.EnsureSuccessStatusCode();

        var response = await client.GetAsync($"/api/pipelines/{pipelineId}/document");
        response.EnsureSuccessStatusCode();
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();

        var prodSteps = doc.GetProperty("productionSteps");
        await Assert.That(prodSteps.GetArrayLength()).IsEqualTo(1);
        var step = prodSteps[0];
        await Assert.That(step.GetProperty("name").GetString()).IsEqualTo("build");
        var config = step.GetProperty("configuration");
        await Assert.That(config.GetProperty("image").GetString()).IsEqualTo("alpine:latest");
        await Assert.That(config.GetProperty("script").GetString()).IsEqualTo("echo build");
    }
}
