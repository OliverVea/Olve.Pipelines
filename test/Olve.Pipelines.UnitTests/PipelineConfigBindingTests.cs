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
        pipelineStore.OnDeleted.Subscribe(cleanup.HandlePipelineDeleted);

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
        await Assert.That(binding.LastDeployedSha).IsNull();
        await Assert.That(Pick(svc.Bindings.GetByPipelineId(pipeline.Id)).Id).IsEqualTo(binding.Id);
        await Assert.That(Pick(svc.Bindings.TryGet(binding.Id)).Id).IsEqualTo(binding.Id);
    }

    [Test]
    public async Task Create_WhenAlreadyBound_Fails()
    {
        var svc = CreateFixture();
        var pipeline = Pick(svc.Pipelines.Create("p"));
        Bind(svc.Bindings, pipeline.Id);

        // One binding per pipeline — a second bind must be rejected.
        await Assert.That(Bind(svc.Bindings, pipeline.Id).Failed).IsTrue();
    }

    [Test]
    public async Task Create_WithBlankRepo_Fails()
    {
        var svc = CreateFixture();
        var pipeline = Pick(svc.Pipelines.Create("p"));

        await Assert.That(svc.Bindings.Create(pipeline.Id, "  ", "main", ".pipelines", null).Failed).IsTrue();
    }

    [Test]
    public async Task SetLastDeployedSha_AdvancesCursor()
    {
        var svc = CreateFixture();
        var pipeline = Pick(svc.Pipelines.Create("p"));
        var binding = Pick(Bind(svc.Bindings, pipeline.Id));

        var updated = Pick(svc.Bindings.SetLastDeployedSha(binding.Id, "abc123"));

        await Assert.That(updated.LastDeployedSha).IsEqualTo("abc123");
        await Assert.That(Pick(svc.Bindings.TryGet(binding.Id)).LastDeployedSha).IsEqualTo("abc123");
    }

    [Test]
    public async Task GetByPipelineId_WhenUnbound_Fails()
    {
        var svc = CreateFixture();
        var pipeline = Pick(svc.Pipelines.Create("draft"));

        // A pipeline with no binding is a draft, per the GitOps design.
        await Assert.That(svc.Bindings.GetByPipelineId(pipeline.Id).Failed).IsTrue();
    }

    [Test]
    public async Task DeletePipeline_CascadesBindingDeletion()
    {
        var svc = CreateFixture();
        var pipeline = Pick(svc.Pipelines.Create("p"));
        Bind(svc.Bindings, pipeline.Id);
        await Assert.That(svc.Bindings.GetByPipelineId(pipeline.Id).Succeeded).IsTrue();

        svc.Pipelines.Delete(pipeline.Id);

        await Assert.That(svc.Bindings.GetByPipelineId(pipeline.Id).Failed).IsTrue();
        await Assert.That(svc.BindingStore.List()).IsEmpty();
    }

    [Test]
    public async Task DeletePipeline_LeavesOtherPipelinesBindingsIntact()
    {
        var svc = CreateFixture();
        var keep = Pick(svc.Pipelines.Create("keep"));
        var drop = Pick(svc.Pipelines.Create("drop"));
        var keepBinding = Pick(Bind(svc.Bindings, keep.Id));
        Bind(svc.Bindings, drop.Id);

        svc.Pipelines.Delete(drop.Id);

        await Assert.That(svc.Bindings.GetByPipelineId(drop.Id).Failed).IsTrue();
        await Assert.That(Pick(svc.Bindings.GetByPipelineId(keep.Id)).Id).IsEqualTo(keepBinding.Id);
    }
}
