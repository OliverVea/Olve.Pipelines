using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Olve.Pipelines.IntegrationTests;

[NotInParallel(Order = int.MaxValue)]
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

    [Test]
    public async Task NullArraysInSnapshot_ToleratedOnStartup()
    {
        var connectionString = Fixture.GetMinioConnectionString();
        if (connectionString is null)
        {
            throw new TUnit.Core.Exceptions.SkipTestException("Requires local MinIO container (not beta S3)");
        }

        // Write a malformed snapshot with null arrays to S3
        var s3Config = new AmazonS3Config
        {
            ServiceURL = connectionString.StartsWith("http") ? connectionString : $"http://{connectionString}",
            ForcePathStyle = true,
        };
        var credentials = new BasicAWSCredentials(Fixture.GetMinioAccessKey(), Fixture.GetMinioSecretKey());
        using var s3 = new AmazonS3Client(credentials, s3Config);

        var malformedSnapshot = """{"Pipelines": null, "ProductionSteps": null, "ProcessingSteps": null}""";
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Fixture.MinioBucket,
            Key = "configuration.json",
            ContentBody = malformedSnapshot,
        });

        // Restart the app — it should tolerate the null arrays
        await Fixture.RestartAsync();

        // Verify the app started successfully
        var client = Fixture.CreateUnauthenticatedHttpClient();
        var healthResponse = await client.GetAsync("/api/health");
        await Assert.That(healthResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Clean up: remove the malformed snapshot so subsequent tests start clean
        await s3.DeleteObjectAsync(Fixture.MinioBucket, "configuration.json");
        await Fixture.RestartAsync();
    }
}
