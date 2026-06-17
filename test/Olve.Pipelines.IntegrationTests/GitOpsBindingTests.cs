using System.Net;
using Olve.Pipelines.Client;

namespace Olve.Pipelines.IntegrationTests;

public class GitOpsBindingTests
{
    [ClassDataSource<AppFixture>(Shared = SharedType.PerAssembly)]
    public required AppFixture Fixture { get; init; }

    // A repo that cannot reconcile, so the background deploy poll never materializes steps or
    // advances status — keeping these HTTP-surface assertions free of background races.
    private static CreatePipelineWithRepoRequest WithRepo(string name) => new()
    {
        Name = name,
        Repo = $"olve-test/nonexistent-{Guid.NewGuid():N}",
        Branch = "main",
        Path = ".pipelines",
    };

    [Test]
    public async Task CreatePipelineWithRepo_CreatesPipelineAndBinding()
    {
        var client = Fixture.CreateApiClient();

        var created = await client.CreatePipelineWithRepo(WithRepo($"with-repo-{Guid.NewGuid():N}"));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(created.Content!.Branch).IsEqualTo("main");
        await Assert.That(created.Content!.Path).IsEqualTo(".pipelines");

        var pipelineId = created.Content!.PipelineId.ToString();
        var binding = await client.GetPipelineBinding(pipelineId);
        await Assert.That(binding.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(binding.Content!.Id).IsEqualTo(created.Content!.Id);
    }

    [Test]
    public async Task BindingStatus_IsReadable_ForBoundPipeline()
    {
        var client = Fixture.CreateApiClient();

        var bound = await client.CreatePipelineWithRepo(WithRepo($"status-{Guid.NewGuid():N}"));
        var pipelineId = bound.Content!.PipelineId.ToString();

        var status = await client.GetPipelineBindingStatus(pipelineId);
        await Assert.That(status.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(status.Content!.PipelineId).IsEqualTo(bound.Content!.PipelineId);
        // A repo with no config declares no secrets.
        await Assert.That(status.Content!.Secrets).IsEmpty();
    }

    [Test]
    public async Task DeletePipeline_CascadesBindingDeletion()
    {
        var client = Fixture.CreateApiClient();

        var bound = await client.CreatePipelineWithRepo(WithRepo($"cascade-{Guid.NewGuid():N}"));
        var pipelineId = bound.Content!.PipelineId.ToString();
        await Assert.That((await client.GetPipelineBinding(pipelineId)).StatusCode).IsEqualTo(HttpStatusCode.OK);

        await client.DeletePipeline(pipelineId);

        var afterDelete = await client.GetPipelineBinding(pipelineId);
        await Assert.That(afterDelete.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }
}
