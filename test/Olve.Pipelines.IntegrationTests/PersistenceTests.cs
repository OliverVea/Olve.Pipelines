using System.Net.Http.Json;
using System.Text.Json;

namespace Olve.Pipelines.IntegrationTests;

public class PersistenceTests
{
    [ClassDataSource<AppFixture>(Shared = SharedType.PerAssembly)]
    public required AppFixture Fixture { get; init; }

    [Test]
    public async Task CreatedPipeline_SurvivesRestart()
    {
        var client = Fixture.CreateAuthenticatedHttpClient();

        // Create a pipeline with a unique name
        var uniqueName = $"persist-test-{Guid.NewGuid():N}";
        var createResponse = await client.PostAsync($"/api/pipelines?name={uniqueName}", null);
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var pipelineId = created.GetProperty("id").GetGuid();

        // Restart the app (container stops and starts, MinIO persists)
        await Fixture.RestartAsync();

        // Verify the pipeline survived the restart
        var freshClient = Fixture.CreateAuthenticatedHttpClient();
        var response = await freshClient.GetAsync("/api/pipelines");
        response.EnsureSuccessStatusCode();
        var pipelines = await response.Content.ReadFromJsonAsync<JsonElement>();

        var found = false;
        for (var i = 0; i < pipelines.GetArrayLength(); i++)
        {
            if (pipelines[i].GetProperty("id").GetGuid() == pipelineId)
            {
                await Assert.That(pipelines[i].GetProperty("name").GetString()).IsEqualTo(uniqueName);
                found = true;
                break;
            }
        }

        await Assert.That(found).IsTrue();
    }
}
