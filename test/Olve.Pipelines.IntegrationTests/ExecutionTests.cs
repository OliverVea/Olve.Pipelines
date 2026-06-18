using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Olve.Pipelines.Client;

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
        BetaGuard.SkipIfNoBeta();

        var client = Fixture.CreateApiClient();
        var http = Fixture.CreateAuthenticatedHttpClient();
        string? pipelineId = null;
        try
        {
            pipelineId = await GitOpsFixtureSeeding.CreateFixturePipelineAsync(Fixture);

            var triggerResponse = await client.TriggerProduction(pipelineId);
            await Assert.That(triggerResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

            var jobs = await WaitForAllJobsTerminal(http, pipelineId);

            var productionJobs = jobs.Where(j => j.GetProperty("type").GetString() == "production").ToList();
            var processingJobs = jobs.Where(j => j.GetProperty("type").GetString() == "processing").ToList();

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
        BetaGuard.SkipIfNoBeta();

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

    private static string GetJobStatusType(JsonElement job) =>
        job.GetProperty("status").GetProperty("type").GetString()!;

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
            var pipelineJobs = await FetchPipelineJobs(http, pipelineId);

            if (pipelineJobs.Count > 0 && pipelineJobs.All(IsTerminal))
            {
                // Wait and recheck to allow cascading to create new jobs
                await Task.Delay(PollInterval);
                var recheckJobs = await FetchPipelineJobs(http, pipelineId);

                if (recheckJobs.All(IsTerminal) && recheckJobs.Count == pipelineJobs.Count)
                    return recheckJobs;

                continue;
            }

            await Task.Delay(PollInterval);
        }

        throw new System.TimeoutException($"Jobs for pipeline {pipelineId} did not reach terminal state within {JobTimeout}");
    }

    // /api/jobs is paginated: { items: [...], pageNumber, ... }. Pull the items array and filter to
    // this pipeline. A test pipeline's job count is tiny, so the default first page holds them all.
    private static async Task<List<JsonElement>> FetchPipelineJobs(HttpClient http, string pipelineId)
    {
        var page = await http.GetFromJsonAsync<JsonElement>("/api/jobs");
        var items = page.ValueKind == JsonValueKind.Array ? page : page.GetProperty("items");
        return items.EnumerateArray()
            .Where(j => j.GetProperty("pipelineId").GetGuid().ToString() == pipelineId)
            .ToList();
    }
}
