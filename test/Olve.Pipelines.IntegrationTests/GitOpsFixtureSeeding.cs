using Olve.Pipelines.Client;

namespace Olve.Pipelines.IntegrationTests;

/// <summary>
/// Helpers for building pipeline shape in integration tests. The API no longer writes shape
/// (GitOps reconcile is the only writer), so tests bind a pipeline to THIS repo at the committed
/// <c>.pipelines-test</c> path and wait for the reconcile loop to materialize the steps.
///
/// The fixture config (<c>.pipelines-test/config.yaml</c>) is a single committed shape: one
/// production step <c>build</c> and one processing step <c>deploy</c>. The repo is public, so the
/// fetch is unauthenticated (no credentials secret) — tests must <see cref="DeleteAsync"/> their
/// pipeline promptly so the deploy poll stops hitting GitHub.
/// </summary>
public static class GitOpsFixtureSeeding
{
    public const string Repo = "OliverVea/Olve.Pipelines";
    public const string FixturePath = ".pipelines-test";
    public const string ProductionStepName = "build";
    public const string ProcessingStepName = "deploy";

    private static readonly TimeSpan ReconcileTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan PollDelay = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Creates a pipeline bound to the fixture repo/path and waits for reconcile to materialize the
    /// committed shape. Returns the pipeline id. The caller owns cleanup via <see cref="DeleteAsync"/>.
    /// </summary>
    public static async Task<string> CreateFixturePipelineAsync(AppFixture fixture)
    {
        var client = fixture.CreateApiClient();

        var created = await client.CreatePipelineWithRepo(new CreatePipelineWithRepoRequest
        {
            Name = $"it-fixture-{Guid.NewGuid():N}",
            Repo = Repo,
            Branch = "main",
            Path = FixturePath,
        });

        if (created.Content is null)
            throw new InvalidOperationException(
                $"Bind to {Repo}@{FixturePath} failed: {(int?)created.StatusCode} {created.Error?.Content}");

        var pipelineId = created.Content.PipelineId.ToString();

        // Reconcile immediately instead of waiting up to a full poll interval (5 min in prod) for the
        // background deploy poll to materialize the shape. Assert it succeeded so an auth/route
        // failure names itself here rather than degrading into an opaque "shape didn't materialize".
        var reconciled = await client.ReconcilePipelineBinding(pipelineId);
        if (!reconciled.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Reconcile-now failed for pipeline {pipelineId}: {(int)reconciled.StatusCode} {reconciled.Error?.Content}");

        await WaitForProductionStepAsync(client, pipelineId);
        return pipelineId;
    }

    /// <summary>Polls until the fixture's production step appears, or throws on timeout.</summary>
    public static async Task WaitForProductionStepAsync(IOlvePipelinesv1 client, string pipelineId)
    {
        var deadline = DateTime.UtcNow + ReconcileTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var steps = await client.ListProductionSteps(pipelineId);
            if (steps.Content is { Count: > 0 })
                return;

            await Task.Delay(PollDelay);
        }

        throw new TimeoutException(
            $"Reconcile did not materialize the fixture shape for pipeline {pipelineId} within {ReconcileTimeout}.");
    }

    /// <summary>Deletes the pipeline (cascades the binding) so the deploy poll stops polling GitHub.</summary>
    public static async Task DeleteAsync(AppFixture fixture, string pipelineId)
        => await fixture.CreateApiClient().DeletePipeline(pipelineId);
}
