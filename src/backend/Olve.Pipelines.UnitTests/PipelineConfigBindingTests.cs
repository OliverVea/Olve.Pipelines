using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Shared;
using Olve.Results;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests;

public class PipelineConfigBindingTests
{
    private sealed record Fixture(
        EntityStore<Pipeline> PipelineStore,
        EntityStore<PipelineConfigBinding> BindingStore,
        PipelineService Pipelines,
        PipelineConfigBindingService Bindings,
        PipelineConfigBindingCleanupService Cleanup);

    private static Fixture CreateFixture()
    {
        var idProvider = new IdProvider();
        var pipelineStore = new EntityStore<Pipeline>([]);
        var bindingStore = new EntityStore<PipelineConfigBinding>([]);

        var pipelineService = new PipelineService(pipelineStore, idProvider);
        var bindingService = new PipelineConfigBindingService(bindingStore, idProvider);
        var cleanup = new PipelineConfigBindingCleanupService(bindingStore);

        // Mirror the production wiring: the binding layer reacts to pipeline deletion.
        pipelineStore.OnDeleted.Subscribe(e => cleanup.HandlePipelineDeleted(e.Id));

        return new Fixture(pipelineStore, bindingStore, pipelineService, bindingService, cleanup);
    }

    private static T Pick<T>(Result<T> result)
    {
        result.TryPickProblems(out _, out var value);
        return value!;
    }

    private static Result<PipelineConfigBinding> Bind(
        PipelineConfigBindingService bindings, Id<Pipeline> pipelineId)
        => bindings.Create(pipelineId, "OliverVea/Olve.Pipelines", "main", ".pipelines", credentialsSecret: null);

    [Test]
    public async Task Create_BindsToPipeline()
    {
        var svc = CreateFixture();
        var pipeline = Pick(svc.Pipelines.Create("p"));

        var binding = Pick(Bind(svc.Bindings, pipeline.Id));

        await Assert.That(binding.PipelineId).IsEqualTo(pipeline.Id);
        await Assert.That(binding.Repo).IsEqualTo("OliverVea/Olve.Pipelines");
        await Assert.That(binding.Branch).IsEqualTo("main");
        await Assert.That(binding.Path).IsEqualTo(".pipelines");
        await Assert.That(binding.LastPushSha).IsNull();
        await Assert.That(Pick(svc.Bindings.GetByPipelineId(pipeline.Id)).Id).IsEqualTo(binding.Id);
        await Assert.That(Pick(svc.Bindings.TryGet(binding.Id)).Id).IsEqualTo(binding.Id);
    }

    [Test]
    public async Task SetCredentialsSecret_UpdatesExistingBinding()
    {
        var svc = CreateFixture();
        var pipeline = Pick(svc.Pipelines.Create("p"));
        await Assert.That(Bind(svc.Bindings, pipeline.Id)).Succeeded(); // binds with credentialsSecret: null

        var updated = Pick(svc.Bindings.SetCredentialsSecret(pipeline.Id, "GITHUB_TOKEN"));

        await Assert.That(updated.CredentialsSecret).IsEqualTo("GITHUB_TOKEN");
        await Assert.That(Pick(svc.Bindings.GetByPipelineId(pipeline.Id)).CredentialsSecret).IsEqualTo("GITHUB_TOKEN");
    }

    [Test]
    public async Task SetBranch_MovesTheBindingAndKeepsItsCursors()
    {
        var svc = CreateFixture();
        var pipeline = Pick(svc.Pipelines.Create("p"));
        var binding = Pick(svc.Bindings.Create(pipeline.Id, "OliverVea/Olve.Pipelines", "v2", ".pipelines", null));
        Pick(svc.Bindings.SetLastPushSha(binding.Id, "abc"));

        var moved = Pick(svc.Bindings.SetBranch(pipeline.Id, "main"));

        await Assert.That(moved.Id).IsEqualTo(binding.Id);
        await Assert.That(moved.Branch).IsEqualTo("main");
        await Assert.That(moved.LastPushSha).IsEqualTo("abc");
        await Assert.That(Pick(svc.Bindings.GetByPipelineId(pipeline.Id)).Branch).IsEqualTo("main");
    }

    [Test]
    public async Task SetBranch_BlankOrUnbound_Fails()
    {
        var svc = CreateFixture();
        var pipeline = Pick(svc.Pipelines.Create("p"));
        await svc.Bindings.SetBranch(pipeline.Id, "main").Problems.IsSingle<NotFoundProblem>($"Pipeline '{pipeline.Id}' has no config binding.");
        await Assert.That(Bind(svc.Bindings, pipeline.Id)).Succeeded();
        await svc.Bindings.SetBranch(pipeline.Id, " ").Problems.IsSingle<ValidationProblem>("branch: Binding requires a branch.");
    }

    [Test]
    public async Task SetCredentialsSecret_BlankClearsIt()
    {
        var svc = CreateFixture();
        var pipeline = Pick(svc.Pipelines.Create("p"));
        await Assert.That(svc.Bindings.Create(pipeline.Id, "OliverVea/Olve.Pipelines", "main", ".pipelines", "GITHUB_TOKEN")).Succeeded();

        var cleared = Pick(svc.Bindings.SetCredentialsSecret(pipeline.Id, "   "));

        await Assert.That(cleared.CredentialsSecret).IsNull();
    }

    [Test]
    public async Task SetCredentialsSecret_WhenUnbound_Fails()
    {
        var svc = CreateFixture();
        var pipeline = Pick(svc.Pipelines.Create("p"));

        await svc.Bindings.SetCredentialsSecret(pipeline.Id, "GITHUB_TOKEN").Problems.IsSingle<NotFoundProblem>($"Pipeline '{pipeline.Id}' has no config binding.");
    }

    [Test]
    public async Task Create_WhenAlreadyBound_Fails()
    {
        var svc = CreateFixture();
        var pipeline = Pick(svc.Pipelines.Create("p"));
        await Assert.That(Bind(svc.Bindings, pipeline.Id)).Succeeded();

        // One binding per pipeline — a second bind must be rejected.
        await Bind(svc.Bindings, pipeline.Id).Problems.IsSingle<ConflictProblem>($"Pipeline '{pipeline.Id}' is already bound to a repository.");
    }

    [Test]
    [Arguments("  ", "main", ".pipelines", "repo: Binding requires a repository ('owner/name').")]
    [Arguments("o/r", " ", ".pipelines", "branch: Binding requires a branch.")]
    [Arguments("o/r", "main", " ", "path: Binding requires a config path (e.g. '.pipelines').")]
    public async Task Create_WithABlankField_Fails(string repo, string branch, string path, string message)
    {
        var svc = CreateFixture();
        var pipeline = Pick(svc.Pipelines.Create("p"));

        await svc.Bindings.Create(pipeline.Id, repo, branch, path, null).Problems.IsSingle<ValidationProblem>(message);
    }

    [Test]
    public async Task SetLastPushSha_AdvancesCursor()
    {
        var svc = CreateFixture();
        var pipeline = Pick(svc.Pipelines.Create("p"));
        var binding = Pick(Bind(svc.Bindings, pipeline.Id));

        var updated = Pick(svc.Bindings.SetLastPushSha(binding.Id, "abc123"));

        await Assert.That(updated.LastPushSha).IsEqualTo("abc123");
        await Assert.That(Pick(svc.Bindings.TryGet(binding.Id)).LastPushSha).IsEqualTo("abc123");
    }

    [Test]
    public async Task GetByPipelineId_WhenUnbound_Fails()
    {
        var svc = CreateFixture();
        var pipeline = Pick(svc.Pipelines.Create("draft"));

        // A pipeline with no binding is a draft, per the GitOps design.
        await svc.Bindings.GetByPipelineId(pipeline.Id).Problems.IsSingle<NotFoundProblem>($"Pipeline '{pipeline.Id}' has no config binding.");
    }

    [Test]
    public async Task DeletePipeline_CascadesBindingDeletion()
    {
        var svc = CreateFixture();
        var pipeline = Pick(svc.Pipelines.Create("p"));
        await Assert.That(Bind(svc.Bindings, pipeline.Id)).Succeeded();
        await Assert.That(svc.Bindings.GetByPipelineId(pipeline.Id)).Succeeded();

        await Assert.That(svc.Pipelines.Delete(pipeline.Id)).Succeeded();

        await svc.Bindings.GetByPipelineId(pipeline.Id).Problems.IsSingle<NotFoundProblem>($"Pipeline '{pipeline.Id}' has no config binding.");
        await Assert.That(svc.BindingStore.List()).IsEmpty();
    }

    [Test]
    public async Task DeletePipeline_LeavesOtherPipelinesBindingsIntact()
    {
        var svc = CreateFixture();
        var keep = Pick(svc.Pipelines.Create("keep"));
        var drop = Pick(svc.Pipelines.Create("drop"));
        var keepBinding = Pick(Bind(svc.Bindings, keep.Id));
        await Assert.That(Bind(svc.Bindings, drop.Id)).Succeeded();

        await Assert.That(svc.Pipelines.Delete(drop.Id)).Succeeded();

        await svc.Bindings.GetByPipelineId(drop.Id).Problems.IsSingle<NotFoundProblem>($"Pipeline '{drop.Id}' has no config binding.");
        await Assert.That(Pick(svc.Bindings.GetByPipelineId(keep.Id)).Id).IsEqualTo(keepBinding.Id);
    }
}
