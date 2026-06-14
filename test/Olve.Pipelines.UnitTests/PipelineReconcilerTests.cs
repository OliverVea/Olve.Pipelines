using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Shared;
using Olve.Results;

namespace Olve.Pipelines.UnitTests;

public class PipelineReconcilerTests
{
    private sealed record Fixture(
        PipelineService Pipelines,
        ProductionStepService Production,
        ProcessingStepService Processing,
        TriggerService Triggers,
        PipelineReconciler Reconciler);

    private static Fixture CreateFixture()
    {
        var idProvider = new IdProvider();
        var pipelineStore = new EntityStore<Pipeline>([]);
        var productionStore = new EntityStore<ProductionStep>([]);
        var processingStore = new EntityStore<ProcessingStep>([]);
        var triggerStore = new EntityStore<Trigger>([]);

        var pipelines = new PipelineService(pipelineStore, idProvider);
        var production = new ProductionStepService(
            productionStore, new AttachmentStore<ProductionStep, StepConfiguration>(productionStore), idProvider);
        var processing = new ProcessingStepService(
            processingStore, new AttachmentStore<ProcessingStep, StepConfiguration>(processingStore), idProvider);
        var triggers = new TriggerService(triggerStore, idProvider);
        var reconciler = new PipelineReconciler(pipelines, production, processing, triggers);

        return new Fixture(pipelines, production, processing, triggers, reconciler);
    }

    private static T Pick<T>(Result<T> result)
    {
        result.TryPickProblems(out _, out var value);
        return value!;
    }

    private static PipelineManifest Manifest(
        ProductionStepDocument[]? production = null,
        ProcessingStepDocument[]? processing = null,
        TriggerDocument[]? triggers = null)
        => new("0.0", "p", null, null, null, production ?? [], processing ?? [], triggers ?? []);

    private static ProductionStepDocument Prod(string name, string script = "echo")
        => new(name, new StepConfigurationDocument("alpine", script, null));

    private static ProcessingStepDocument Proc(string name, string script = "echo")
        => new(name, new StepConfigurationDocument("alpine", script, null));

    [Test]
    public async Task Reconcile_FromEmpty_CreatesEverything()
    {
        var f = CreateFixture();
        var pipeline = Pick(f.Pipelines.Create("p"));

        var result = f.Reconciler.Reconcile(pipeline.Id, Manifest(
            production: [Prod("build")],
            processing: [Proc("deploy")],
            triggers: [new TriggerDocument("redeploy", new ProcessingTargetDocument("deploy"))]));

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Pick(f.Production.GetByPipelineId(pipeline.Id)).Length).IsEqualTo(1);
        await Assert.That(Pick(f.Processing.GetByPipelineId(pipeline.Id)).Length).IsEqualTo(1);

        var triggers = Pick(f.Triggers.GetByPipelineId(pipeline.Id));
        await Assert.That(triggers.Length).IsEqualTo(1);
        await Assert.That(triggers[0].Target).IsTypeOf<ProcessingTriggerTarget>();
    }

    [Test]
    public async Task Reconcile_Idempotent_KeepsStepIds()
    {
        var f = CreateFixture();
        var pipeline = Pick(f.Pipelines.Create("p"));
        var manifest = Manifest(production: [Prod("build")], processing: [Proc("deploy")]);

        f.Reconciler.Reconcile(pipeline.Id, manifest);
        var firstId = Pick(f.Production.GetByPipelineId(pipeline.Id))[0].Id;

        f.Reconciler.Reconcile(pipeline.Id, manifest);
        var secondId = Pick(f.Production.GetByPipelineId(pipeline.Id))[0].Id;

        await Assert.That(secondId).IsEqualTo(firstId);
    }

    [Test]
    public async Task Reconcile_UpdatesConfig_KeepsId()
    {
        var f = CreateFixture();
        var pipeline = Pick(f.Pipelines.Create("p"));

        f.Reconciler.Reconcile(pipeline.Id, Manifest(production: [Prod("build", "old")]));
        var id = Pick(f.Production.GetByPipelineId(pipeline.Id))[0].Id;

        f.Reconciler.Reconcile(pipeline.Id, Manifest(production: [Prod("build", "new")]));

        await Assert.That(Pick(f.Production.GetByPipelineId(pipeline.Id))[0].Id).IsEqualTo(id);
        await Assert.That(Pick(f.Production.TryGetConfiguration(id)).Script).IsEqualTo("new");
    }

    [Test]
    public async Task Reconcile_RemovesStepNotInManifest()
    {
        var f = CreateFixture();
        var pipeline = Pick(f.Pipelines.Create("p"));

        // Simulate a manual API addition that the manifest does not declare.
        f.Production.Create(pipeline.Id, "manual");
        f.Reconciler.Reconcile(pipeline.Id, Manifest(production: [Prod("build")]));

        var live = Pick(f.Production.GetByPipelineId(pipeline.Id));
        await Assert.That(live.Length).IsEqualTo(1);
        await Assert.That(live[0].Name).IsEqualTo("build");
    }

    [Test]
    public async Task Reconcile_Rename_DeletesAndRecreates()
    {
        var f = CreateFixture();
        var pipeline = Pick(f.Pipelines.Create("p"));

        f.Reconciler.Reconcile(pipeline.Id, Manifest(production: [Prod("old")]));
        var oldId = Pick(f.Production.GetByPipelineId(pipeline.Id))[0].Id;

        f.Reconciler.Reconcile(pipeline.Id, Manifest(production: [Prod("new")]));
        var live = Pick(f.Production.GetByPipelineId(pipeline.Id));

        await Assert.That(live.Length).IsEqualTo(1);
        await Assert.That(live[0].Name).IsEqualTo("new");
        await Assert.That(live[0].Id).IsNotEqualTo(oldId);
    }

    [Test]
    public async Task Reconcile_ProcessingSteps_OrderedByListIndex()
    {
        var f = CreateFixture();
        var pipeline = Pick(f.Pipelines.Create("p"));

        f.Reconciler.Reconcile(pipeline.Id, Manifest(processing: [Proc("a"), Proc("b")]));
        // Reverse order in the manifest — the live order must follow.
        f.Reconciler.Reconcile(pipeline.Id, Manifest(processing: [Proc("b"), Proc("a")]));

        var live = Pick(f.Processing.GetByPipelineId(pipeline.Id));
        await Assert.That(live[0].Name).IsEqualTo("b");
        await Assert.That(live[1].Name).IsEqualTo("a");
    }

    [Test]
    public async Task Reconcile_DeletesTriggerNotInManifest()
    {
        var f = CreateFixture();
        var pipeline = Pick(f.Pipelines.Create("p"));

        // Manually add a trigger (delete-to-match should remove it).
        f.Triggers.Create(pipeline.Id, "manual", new ProductionTriggerTarget());
        f.Reconciler.Reconcile(pipeline.Id, Manifest(
            triggers: [new TriggerDocument("keep", new ProductionTargetDocument())]));

        var live = Pick(f.Triggers.GetByPipelineId(pipeline.Id));
        await Assert.That(live.Length).IsEqualTo(1);
        await Assert.That(live[0].Name).IsEqualTo("keep");
    }

    [Test]
    public async Task Reconcile_DanglingProcessingTrigger_Fails()
    {
        var f = CreateFixture();
        var pipeline = Pick(f.Pipelines.Create("p"));

        var result = f.Reconciler.Reconcile(pipeline.Id, Manifest(
            triggers: [new TriggerDocument("t", new ProcessingTargetDocument("ghost"))]));

        await Assert.That(result.Failed).IsTrue();
    }
}
