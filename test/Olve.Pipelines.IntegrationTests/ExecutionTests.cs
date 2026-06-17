using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Olve.Pipelines.Client;
using TUnit.Core.Exceptions;

namespace Olve.Pipelines.IntegrationTests;

/// <summary>
/// End-to-end execution against a real K8s cluster (beta-gated). Shape is seeded by binding to the
/// committed <c>.pipelines-test</c> config (one production step that writes an artifact, one
/// processing step that consumes it) and letting reconcile materialize it — the API no longer
/// writes shape. The committed fixture is a single happy-path cascade, so the bespoke-script
/// variations (failing step, etc.) that the old API-built tests covered now live as unit tests
/// over the cascade rules (see DownstreamTriggerServiceTests).
/// </summary>
public class ExecutionTests
{
    [ClassDataSource<AppFixture>(Shared = SharedType.PerAssembly)]
    public required AppFixture Fixture { get; init; }

    private static readonly TimeSpan JobTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    [Test]
    public async Task TriggerProduction_CascadesThroughProcessingStep()
    {
        SkipIfNoBetaK8s();

        var client = Fixture.CreateApiClient();
        var http = Fixture.CreateAuthenticatedHttpClient();
        string? pipelineId = null;
        try
        {
            pipelineId = await GitOpsFixtureSeeding.CreateFixturePipelineAsync(Fixture);

            var triggerResponse = await client.TriggerProduction(pipelineId);
            await Assert.That(triggerResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

            var jobs = await WaitForAllJobsTerminal(http, pipelineId);

            var productionJobs = jobs.Where(j => j.GetProperty("$type").GetString() == "production").ToList();
            var processingJobs = jobs.Where(j => j.GetProperty("$type").GetString() == "processing").ToList();

            await Assert.That(productionJobs).Count().IsEqualTo(1);
            await Assert.That(GetJobStatusType(productionJobs[0])).IsEqualTo("done");

            await Assert.That(processingJobs).Count().IsEqualTo(1);
            await Assert.That(GetJobStatusType(processingJobs[0])).IsEqualTo("done");
        }
        finally
        {
            if (pipelineId is not null)
                await GitOpsFixtureSeeding.DeleteAsync(Fixture, pipelineId);
        }
    }

    [Test]
    public async Task TriggerProduction_ArtifactBundleCompletesOnSuccess()
    {
        SkipIfNoBetaK8s();

        var http = Fixture.CreateAuthenticatedHttpClient();
        string? pipelineId = null;
        try
        {
            pipelineId = await GitOpsFixtureSeeding.CreateFixturePipelineAsync(Fixture);

            var triggerResponse = await http.PostAsync($"/api/pipelines/{pipelineId}/trigger/production", null);
            await Assert.That(triggerResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

            var jobGroup = await triggerResponse.Content.ReadFromJsonAsync<JsonElement>();
            var bundleId = jobGroup.GetProperty("artifactBundleId").GetGuid();

            await WaitForAllJobsTerminal(http, pipelineId);

            var bundleResponse = await http.GetFromJsonAsync<JsonElement>($"/api/artifact-bundles/{bundleId}");
            await Assert.That(bundleResponse.GetProperty("status").GetInt32()).IsEqualTo(1);
        }
        finally
        {
            if (pipelineId is not null)
                await GitOpsFixtureSeeding.DeleteAsync(Fixture, pipelineId);
        }
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
