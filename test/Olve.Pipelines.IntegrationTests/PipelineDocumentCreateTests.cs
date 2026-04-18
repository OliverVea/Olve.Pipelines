using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Olve.Pipelines.IntegrationTests;

public class PipelineDocumentCreateTests
{
    [ClassDataSource<AppFixture>(Shared = SharedType.PerAssembly)]
    public required AppFixture Fixture { get; init; }

    [Test]
    public async Task PostFromDocument_CreatesPipelineWithStepsAndTriggers()
    {
        var client = Fixture.CreateAuthenticatedHttpClient();
        var name = $"doc-create-{Guid.NewGuid():N}";

        var body = new
        {
            apiVersion = "0.0",
            name,
            productionSteps = new[]
            {
                new
                {
                    name = "build",
                    configuration = new { image = "alpine:latest", script = "echo build", environmentVariables = (object?)null },
                },
            },
            processingSteps = new[]
            {
                new { name = "deploy", configuration = (object?)null },
            },
            triggers = new[]
            {
                new
                {
                    name = "post-build",
                    target = new { type = "processing", processingStepName = "deploy" },
                },
            },
        };

        var response = await client.PostAsJsonAsync("/api/pipelines/from-document", body);
        response.EnsureSuccessStatusCode();

        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(doc.GetProperty("apiVersion").GetString()).IsEqualTo("0.0");
        await Assert.That(doc.GetProperty("name").GetString()).IsEqualTo(name);
        await Assert.That(doc.GetProperty("productionSteps").GetArrayLength()).IsEqualTo(1);
        await Assert.That(doc.GetProperty("processingSteps").GetArrayLength()).IsEqualTo(1);
        await Assert.That(doc.GetProperty("triggers").GetArrayLength()).IsEqualTo(1);

        var trigger = doc.GetProperty("triggers")[0];
        await Assert.That(trigger.GetProperty("target").GetProperty("type").GetString()).IsEqualTo("processing");
        await Assert.That(trigger.GetProperty("target").GetProperty("processingStepName").GetString()).IsEqualTo("deploy");
    }

    [Test]
    public async Task PostFromDocument_DanglingProcessingReference_Returns4xx()
    {
        var client = Fixture.CreateAuthenticatedHttpClient();

        var body = new
        {
            apiVersion = "0.0",
            name = $"doc-bad-{Guid.NewGuid():N}",
            productionSteps = Array.Empty<object>(),
            processingSteps = Array.Empty<object>(),
            triggers = new[]
            {
                new
                {
                    name = "broken",
                    target = new { type = "processing", processingStepName = "does-not-exist" },
                },
            },
        };

        var response = await client.PostAsJsonAsync("/api/pipelines/from-document", body);
        await Assert.That((int)response.StatusCode).IsBetween(400, 499);
    }

    [Test]
    public async Task PostFromDocument_IncompatibleApiVersion_Returns4xx()
    {
        var client = Fixture.CreateAuthenticatedHttpClient();

        var body = new
        {
            apiVersion = "1.0",
            name = $"doc-v1-{Guid.NewGuid():N}",
            productionSteps = Array.Empty<object>(),
            processingSteps = Array.Empty<object>(),
            triggers = Array.Empty<object>(),
        };

        var response = await client.PostAsJsonAsync("/api/pipelines/from-document", body);
        await Assert.That((int)response.StatusCode).IsBetween(400, 499);
    }

    [Test]
    public async Task PostFromDocument_ThenGetDocument_RoundTrips()
    {
        var client = Fixture.CreateAuthenticatedHttpClient();
        var name = $"doc-roundtrip-{Guid.NewGuid():N}";

        var body = new
        {
            apiVersion = "0.0",
            name,
            productionSteps = new[]
            {
                new { name = "compile", configuration = new { image = "alpine", script = "echo ok", environmentVariables = (object?)null } },
            },
            processingSteps = Array.Empty<object>(),
            triggers = Array.Empty<object>(),
        };

        var createResp = await client.PostAsJsonAsync("/api/pipelines/from-document", body);
        createResp.EnsureSuccessStatusCode();
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();

        var listResp = await client.GetAsync("/api/pipelines");
        listResp.EnsureSuccessStatusCode();
        var pipelines = await listResp.Content.ReadFromJsonAsync<JsonElement>();
        Guid? pipelineId = null;
        foreach (var p in pipelines.EnumerateArray())
        {
            if (p.GetProperty("name").GetString() == name)
            {
                pipelineId = p.GetProperty("id").GetGuid();
                break;
            }
        }
        await Assert.That(pipelineId).IsNotNull();

        var getResp = await client.GetAsync($"/api/pipelines/{pipelineId}/document");
        getResp.EnsureSuccessStatusCode();
        var fetched = await getResp.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(fetched.GetProperty("name").GetString()).IsEqualTo(name);
        await Assert.That(fetched.GetProperty("productionSteps")[0].GetProperty("name").GetString()).IsEqualTo("compile");
        await Assert.That(fetched.GetProperty("productionSteps")[0].GetProperty("configuration").GetProperty("image").GetString()).IsEqualTo("alpine");
    }
}
