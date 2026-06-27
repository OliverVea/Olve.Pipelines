using System.Net;
using Olve.Pipelines.Client;

namespace Olve.Pipelines.IntegrationTests;

public class GitOpsBindingTests
{
    [ClassDataSource<AppFixture>(Shared = SharedType.PerAssembly)]
    public required AppFixture Fixture { get; init; }

    // A repo that cannot reconcile, so the background deploy poll never materializes steps or
    // advances status — keeping these HTTP-surface assertions free of background races.
    private static CreatePipelineWithRepoRequest WithRepo() => new()
    {
        Repo = $"olve-test/nonexistent-{Guid.NewGuid():N}",
        Branch = "main",
        Path = ".pipelines",
    };

    [Test]
    public async Task CreatePipelineWithRepo_CreatesPipelineAndBinding()
    {
        BetaGuard.SkipIfNoBeta();

        var client = Fixture.CreateApiClient();

        var created = await client.CreatePipelineWithRepo(WithRepo());
        string? pipelineId = null;
        try
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(created.Content!.Branch).IsEqualTo("main");
            await Assert.That(created.Content!.Path).IsEqualTo(".pipelines");

            pipelineId = created.Content!.PipelineId.ToString();
            var binding = await client.GetPipelineBinding(pipelineId);
            await Assert.That(binding.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(binding.Content!.Id).IsEqualTo(created.Content!.Id);
        }
        finally
        {
            // MUST delete: on the shared beta instance a leaked binding to a nonexistent repo gets
            // polled every cycle forever, draining the unauthenticated GitHub rate limit.
            if (pipelineId is not null)
                await client.DeletePipeline(pipelineId);
        }
    }

    [Test]
    public async Task BindingStatus_IsReadable_ForBoundPipeline()
    {
        BetaGuard.SkipIfNoBeta();

        var client = Fixture.CreateApiClient();

        var bound = await client.CreatePipelineWithRepo(WithRepo());
        var pipelineId = bound.Content!.PipelineId.ToString();
        try
        {
            var status = await client.GetPipelineBindingStatus(pipelineId);
            await Assert.That(status.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(status.Content!.PipelineId).IsEqualTo(bound.Content!.PipelineId);
            // A repo with no config declares no secrets.
            await Assert.That(status.Content!.Secrets).IsEmpty();
        }
        finally
        {
            // See above — leaked bindings poll GitHub forever and exhaust the rate limit.
            await client.DeletePipeline(pipelineId);
        }
    }

    [Test]
    public async Task DeletePipeline_CascadesBindingDeletion()
    {
        BetaGuard.SkipIfNoBeta();

        var client = Fixture.CreateApiClient();

        var bound = await client.CreatePipelineWithRepo(WithRepo());
        var pipelineId = bound.Content!.PipelineId.ToString();
        await Assert.That((await client.GetPipelineBinding(pipelineId)).StatusCode).IsEqualTo(HttpStatusCode.OK);

        await client.DeletePipeline(pipelineId);

        var afterDelete = await client.GetPipelineBinding(pipelineId);
        await Assert.That(afterDelete.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }
}
