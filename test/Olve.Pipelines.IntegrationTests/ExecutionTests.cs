using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Olve.Pipelines.Client;
using TUnit.Core.Exceptions;

namespace Olve.Pipelines.IntegrationTests;

public class ExecutionTests
{
    [ClassDataSource<AppFixture>(Shared = SharedType.PerAssembly)]
    public required AppFixture Fixture { get; init; }

    private static readonly TimeSpan JobTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    [Test]
    public async Task TriggerProduction_CascadesThroughAllProcessingSteps()
    {
        SkipIfNoBetaK8s();

        var client = Fixture.CreateApiClient();
        var http = Fixture.CreateAuthenticatedHttpClient();
        var pipelineId = await CreatePipelineWithSteps(client,
            productionScript: "echo hello > /output/hello.txt",
            processingScripts: ["cat /input/*/hello.txt", "echo done"]);

        var triggerResponse = await client.TriggerProduction(pipelineId);
        await Assert.That(triggerResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var jobs = await WaitForAllJobsTerminal(http, pipelineId);

        var productionJobs = jobs.Where(j => j.GetProperty("$type").GetString() == "production").ToList();
        var processingJobs = jobs.Where(j => j.GetProperty("$type").GetString() == "processing").ToList();

        await Assert.That(productionJobs).Count().IsEqualTo(1);
        await Assert.That(GetJobStatusType(productionJobs[0])).IsEqualTo("done");

        await Assert.That(processingJobs).Count().IsEqualTo(2);
        await Assert.That(GetJobStatusType(processingJobs[0])).IsEqualTo("done");
        await Assert.That(GetJobStatusType(processingJobs[1])).IsEqualTo("done");
    }

    [Test]
    public async Task TriggerProduction_WithFailingStep_DoesNotCascade()
    {
        SkipIfNoBetaK8s();

        var client = Fixture.CreateApiClient();
        var http = Fixture.CreateAuthenticatedHttpClient();
        var pipelineId = await CreatePipelineWithSteps(client,
            productionScript: "exit 1",
            processingScripts: ["echo should-not-run"]);

        var triggerResponse = await client.TriggerProduction(pipelineId);
        await Assert.That(triggerResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var jobs = await WaitForAllJobsTerminal(http, pipelineId);

        var productionJobs = jobs.Where(j => j.GetProperty("$type").GetString() == "production").ToList();
        var processingJobs = jobs.Where(j => j.GetProperty("$type").GetString() == "processing").ToList();

        await Assert.That(productionJobs).Count().IsEqualTo(1);
        await Assert.That(GetJobStatusType(productionJobs[0])).IsEqualTo("failed");

        await Assert.That(processingJobs).Count().IsEqualTo(0);
    }

    [Test]
    public async Task TriggerProduction_ArtifactBundleCompletesOnSuccess()
    {
        SkipIfNoBetaK8s();

        var client = Fixture.CreateApiClient();
        var http = Fixture.CreateAuthenticatedHttpClient();
        var pipelineId = await CreatePipelineWithSteps(client,
            productionScript: "echo artifact-content > /output/data.txt",
            processingScripts: ["cat /input/*/data.txt"]);

        var triggerResponse = await http.PostAsync($"/api/pipelines/{pipelineId}/trigger/production", null);
        await Assert.That(triggerResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var jobGroup = await triggerResponse.Content.ReadFromJsonAsync<JsonElement>();
        var bundleId = jobGroup.GetProperty("artifactBundleId").GetGuid();

        await WaitForAllJobsTerminal(http, pipelineId);

        var bundleResponse = await http.GetFromJsonAsync<JsonElement>($"/api/artifact-bundles/{bundleId}");
        await Assert.That(bundleResponse.GetProperty("status").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task TriggerProduction_LogsAvailableAfterCompletion()
    {
        SkipIfNoBetaK8s();

        var client = Fixture.CreateApiClient();
        var http = Fixture.CreateAuthenticatedHttpClient();
        var pipelineId = await CreatePipelineWithSteps(client,
            productionScript: "echo expected-log-output",
            processingScripts: []);

        var triggerResponse = await client.TriggerProduction(pipelineId);
        await Assert.That(triggerResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var jobs = await WaitForAllJobsTerminal(http, pipelineId);
        var productionJob = jobs.Single(j => j.GetProperty("$type").GetString() == "production");
        var jobId = productionJob.GetProperty("id").GetGuid();

        var logsResponse = await http.GetAsync($"/api/jobs/{jobId}/logs");
        await Assert.That(logsResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var logs = await logsResponse.Content.ReadAsStringAsync();
        await Assert.That(logs).Contains("expected-log-output");
    }

    private async Task<string> CreatePipelineWithSteps(
        IOlvePipelinesv1 client,
        string productionScript,
        string[] processingScripts)
    {
        var pipeline = await client.CreatePipeline($"exec-test-{Guid.NewGuid():N}");
        var pipelineId = pipeline.Content!.Id.ToString();

        var prodStep = await client.CreateProductionStep(pipelineId, new CreateProductionStepRequest { Name = "build" });
        await client.SetProductionStepConfiguration(prodStep.Content!.Id.ToString(), new SetStepConfigurationRequest
        {
            Image = "alpine:latest",
            Script = productionScript,
        });

        for (var i = 0; i < processingScripts.Length; i++)
        {
            var procStep = await client.CreateProcessingStep(pipelineId, new CreateProcessingStepRequest { Name = $"process-{i}" });
            await client.SetProcessingStepConfiguration(procStep.Content!.Id.ToString(), new SetStepConfigurationRequest
            {
                Image = "alpine:latest",
                Script = processingScripts[i],
            });
            await client.UpdateProcessingStepOrder(procStep.Content!.Id.ToString(), new UpdateOrderRequest { Order = i });
        }

        return pipelineId;
    }

    private static void SkipIfNoBetaK8s()
    {
        if (!AppFixture.UseBetaK8s)
            throw new SkipTestException("Requires beta K8s environment");
    }

    private static string GetJobStatusType(JsonElement job) =>
        job.GetProperty("status").GetProperty("$type").GetString()!;

    private static readonly string[] TerminalStatuses = ["done", "failed", "cancelled", "obsolete"];

    private static bool IsTerminal(JsonElement job) =>
        TerminalStatuses.Contains(GetJobStatusType(job));

    private async Task<List<JsonElement>> WaitForAllJobsTerminal(
        HttpClient http,
        string pipelineId)
    {
        var deadline = DateTime.UtcNow + JobTimeout;

        while (DateTime.UtcNow < deadline)
        {
            var allJobs = await http.GetFromJsonAsync<JsonElement>("/api/jobs");
            var pipelineJobs = allJobs.EnumerateArray()
                .Where(j => j.GetProperty("pipelineId").GetGuid().ToString() == pipelineId)
                .ToList();

            if (pipelineJobs.Count > 0 && pipelineJobs.All(IsTerminal))
            {
                // Wait and recheck to allow cascading to create new jobs
                await Task.Delay(PollInterval);
                var recheck = await http.GetFromJsonAsync<JsonElement>("/api/jobs");
                var recheckJobs = recheck.EnumerateArray()
                    .Where(j => j.GetProperty("pipelineId").GetGuid().ToString() == pipelineId)
                    .ToList();

                if (recheckJobs.All(IsTerminal) && recheckJobs.Count == pipelineJobs.Count)
                    return recheckJobs;

                continue;
            }

            await Task.Delay(PollInterval);
        }

        throw new System.TimeoutException($"Jobs for pipeline {pipelineId} did not reach terminal state within {JobTimeout}");
    }
}
