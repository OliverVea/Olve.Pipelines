using Olve.Pipelines.Client;

namespace Olve.Pipelines.IntegrationTests;

/// <summary>
/// End-to-end GitOps reconcile: bind a pipeline to the committed <c>.pipelines-test</c> config,
/// let the GitHub-backed reconcile loop run, and confirm the shape materializes in the container.
///
/// NOTE: this (and the rest of the Testcontainers integration suite) needs Docker + network egress
/// to GitHub and is opt-in (<c>-p:RunIntegrationTests=true</c>). The in-process reconcile logic is
/// also covered without Docker by <c>PipelinesTestFixtureConfigTests</c> in the unit suite. Planned
/// follow-up: run server-dependent tests against the beta instance instead of Testcontainers — see
/// the <c>project_pipeline_self_testing</c> memory.
/// </summary>
public class GitOpsReconcileTests
{
    [ClassDataSource<AppFixture>(Shared = SharedType.PerAssembly)]
    public required AppFixture Fixture { get; init; }

    [Test]
    public async Task BindToFixtureRepo_ReconcileMaterializesCommittedShape()
    {
        var client = Fixture.CreateApiClient();
        string? pipelineId = null;
        try
        {
            pipelineId = await GitOpsFixtureSeeding.CreateFixturePipelineAsync(Fixture);

            var production = await client.ListProductionSteps(pipelineId);
            await Assert.That(production.Content!.Select(s => s.Name))
                .Contains(GitOpsFixtureSeeding.ProductionStepName);

            var processing = await client.ListProcessingSteps(pipelineId);
            await Assert.That(processing.Content!.Select(s => s.Name))
                .Contains(GitOpsFixtureSeeding.ProcessingStepName);
        }
        finally
        {
            if (pipelineId is not null)
                await GitOpsFixtureSeeding.DeleteAsync(Fixture, pipelineId);
        }
    }
}
