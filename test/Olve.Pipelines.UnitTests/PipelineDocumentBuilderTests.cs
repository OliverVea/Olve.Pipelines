using System.Text.Json;
using Olve.Pipelines;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Shared;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests;

public class PipelineDocumentBuilderTests
{
    private sealed record Fixture(
        PipelineDocumentBuilder Builder,
        EntityStore<Pipeline> Pipelines,
        EntityStore<ProductionStep> ProductionSteps,
        EntityStore<ProcessingStep> ProcessingSteps,
        EntityStore<Trigger> Triggers,
        AttachmentStore<ProductionStep, StepConfiguration> ProductionConfigs,
        AttachmentStore<ProcessingStep, StepConfiguration> ProcessingConfigs,
        IdProvider IdProvider);

    private static Fixture CreateFixture()
    {
        var pipelineStore = new EntityStore<Pipeline>([]);
        var productionStore = new EntityStore<ProductionStep>([]);
        var processingStore = new EntityStore<ProcessingStep>([]);
        var triggerStore = new EntityStore<Trigger>([]);
        var productionConfigs = new AttachmentStore<ProductionStep, StepConfiguration>(productionStore);
        var processingConfigs = new AttachmentStore<ProcessingStep, StepConfiguration>(processingStore);
        var idProvider = new IdProvider();

        var pipelineService = new PipelineService(pipelineStore, idProvider);
        var productionService = new ProductionStepService(productionStore, productionConfigs, idProvider);
        var processingService = new ProcessingStepService(processingStore, processingConfigs, idProvider);
        var triggerService = new TriggerService(triggerStore, idProvider);

        var builder = new PipelineDocumentBuilder(
            pipelineService,
            productionService,
            processingService,
            triggerService,
            productionConfigs,
            processingConfigs,
            processingStore);

        return new Fixture(
            builder,
            pipelineStore,
            productionStore,
            processingStore,
            triggerStore,
            productionConfigs,
            processingConfigs,
            idProvider);
    }

    [Test]
    public async Task Build_UnknownPipeline_Fails()
    {
        var f = CreateFixture();
        var result = f.Builder.Build(Id.New<Pipeline>());
        await Assert.That(result).Failed();
    }

    [Test]
    public async Task Build_EmptyPipeline_ReturnsEmptyLists()
    {
        var f = CreateFixture();
        var pipelineId = f.IdProvider.Create<Pipeline>();
        f.Pipelines.Set(new Pipeline(pipelineId, "p"));

        var result = f.Builder.Build(pipelineId);

        await Assert.That(result).Succeeded();
        result.TryPickProblems(out _, out var doc);
        await Assert.That(doc!.ApiVersion).IsEqualTo("0.0");
        await Assert.That(doc.Name).IsEqualTo("p");
        await Assert.That(doc.ProductionSteps.Count).IsEqualTo(0);
        await Assert.That(doc.ProcessingSteps.Count).IsEqualTo(0);
        await Assert.That(doc.Triggers.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Build_StepsWithAndWithoutConfig_Roundtrip()
    {
        var f = CreateFixture();
        var pipelineId = f.IdProvider.Create<Pipeline>();
        f.Pipelines.Set(new Pipeline(pipelineId, "p"));

        var prodConfigured = new ProductionStep(f.IdProvider.Create<ProductionStep>(), "build", pipelineId);
        var prodBare = new ProductionStep(f.IdProvider.Create<ProductionStep>(), "bare", pipelineId);
        f.ProductionSteps.Set(prodConfigured);
        f.ProductionSteps.Set(prodBare);
        f.ProductionConfigs.Set(prodConfigured.Id,
            new StepConfiguration("img", "echo hi", new Dictionary<string, string> { ["K"] = "V" }));

        var procConfigured = new ProcessingStep(f.IdProvider.Create<ProcessingStep>(), "deploy", pipelineId, 0);
        f.ProcessingSteps.Set(procConfigured);
        f.ProcessingConfigs.Set(procConfigured.Id, new StepConfiguration("img2", "echo done", null));

        var result = f.Builder.Build(pipelineId);

        await Assert.That(result).Succeeded();
        result.TryPickProblems(out _, out var doc);
        await Assert.That(doc!.ProductionSteps.Count).IsEqualTo(2);
        var byName = doc.ProductionSteps.ToDictionary(s => s.Name);
        await Assert.That(byName["build"].Configuration!.Image).IsEqualTo("img");
        await Assert.That(byName["build"].Configuration!.EnvironmentVariables!["K"]).IsEqualTo("V");
        await Assert.That(byName["bare"].Configuration).IsNull();
        await Assert.That(doc.ProcessingSteps[0].Configuration!.Script).IsEqualTo("echo done");
    }

    [Test]
    public async Task Build_ProcessingSteps_SortedByOrderAscending()
    {
        var f = CreateFixture();
        var pipelineId = f.IdProvider.Create<Pipeline>();
        f.Pipelines.Set(new Pipeline(pipelineId, "p"));

        f.ProcessingSteps.Set(new ProcessingStep(f.IdProvider.Create<ProcessingStep>(), "third", pipelineId, 20));
        f.ProcessingSteps.Set(new ProcessingStep(f.IdProvider.Create<ProcessingStep>(), "first", pipelineId, 0));
        f.ProcessingSteps.Set(new ProcessingStep(f.IdProvider.Create<ProcessingStep>(), "second", pipelineId, 10));

        var result = f.Builder.Build(pipelineId);

        result.TryPickProblems(out _, out var doc);
        await Assert.That(doc!.ProcessingSteps.Select(s => s.Name).ToArray())
            .IsEquivalentTo(new[] { "first", "second", "third" });
    }

    [Test]
    public async Task Build_AllThreeTargetTypes_MapToDiscriminatedSubtypes()
    {
        var f = CreateFixture();
        var pipelineId = f.IdProvider.Create<Pipeline>();
        f.Pipelines.Set(new Pipeline(pipelineId, "p"));

        var procStep = new ProcessingStep(f.IdProvider.Create<ProcessingStep>(), "deploy", pipelineId, 0);
        f.ProcessingSteps.Set(procStep);

        f.Triggers.Set(new Trigger(
            f.IdProvider.Create<Trigger>(), pipelineId, "prod-webhook",
            new ProductionTriggerTarget(), "s1", DateTimeOffset.UtcNow));
        f.Triggers.Set(new Trigger(
            f.IdProvider.Create<Trigger>(), pipelineId, "proc-webhook",
            new ProcessingTriggerTarget(procStep.Id), "s2", DateTimeOffset.UtcNow));
        f.Triggers.Set(new Trigger(
            f.IdProvider.Create<Trigger>(), pipelineId, "poller",
            new PollTriggerTarget("https://x", null, "$.tag", 120), "s3", DateTimeOffset.UtcNow));

        var result = f.Builder.Build(pipelineId);
        result.TryPickProblems(out _, out var doc);

        var byName = doc!.Triggers.ToDictionary(t => t.Name);
        await Assert.That(byName["prod-webhook"].Target).IsTypeOf<ProductionTargetDocument>();
        await Assert.That(byName["proc-webhook"].Target).IsTypeOf<ProcessingTargetDocument>();
        await Assert.That(((ProcessingTargetDocument)byName["proc-webhook"].Target).ProcessingStepName).IsEqualTo("deploy");
        var poll = (PollTargetDocument)byName["poller"].Target;
        await Assert.That(poll.Url).IsEqualTo("https://x");
        await Assert.That(poll.ValuePath).IsEqualTo("$.tag");
        await Assert.That(poll.IntervalSeconds).IsEqualTo(120);
    }

    [Test]
    public async Task Build_DanglingProcessingTriggerTarget_Fails()
    {
        var f = CreateFixture();
        var pipelineId = f.IdProvider.Create<Pipeline>();
        f.Pipelines.Set(new Pipeline(pipelineId, "p"));

        f.Triggers.Set(new Trigger(
            f.IdProvider.Create<Trigger>(), pipelineId, "broken",
            new ProcessingTriggerTarget(Id.New<ProcessingStep>()), "s", DateTimeOffset.UtcNow));

        var result = f.Builder.Build(pipelineId);
        await Assert.That(result).Failed();
    }

    [Test]
    public async Task Build_JsonRoundtrip_PreservesDocument()
    {
        var f = CreateFixture();
        var pipelineId = f.IdProvider.Create<Pipeline>();
        f.Pipelines.Set(new Pipeline(pipelineId, "p"));

        var prod = new ProductionStep(f.IdProvider.Create<ProductionStep>(), "build", pipelineId);
        f.ProductionSteps.Set(prod);
        f.ProductionConfigs.Set(prod.Id, new StepConfiguration("img", "s", null));

        var procStep = new ProcessingStep(f.IdProvider.Create<ProcessingStep>(), "deploy", pipelineId, 0);
        f.ProcessingSteps.Set(procStep);

        f.Triggers.Set(new Trigger(
            f.IdProvider.Create<Trigger>(), pipelineId, "poll",
            new PollTriggerTarget("https://x", null, "$.t", 60), "s", DateTimeOffset.UtcNow));

        var result = f.Builder.Build(pipelineId);
        result.TryPickProblems(out _, out var doc);

        var json = JsonSerializer.Serialize(doc);
        var roundtripped = JsonSerializer.Deserialize<PipelineDocument>(json)!;

        await Assert.That(roundtripped.ApiVersion).IsEqualTo("0.0");
        await Assert.That(roundtripped.Name).IsEqualTo("p");
        await Assert.That(roundtripped.ProductionSteps[0].Name).IsEqualTo("build");
        await Assert.That(roundtripped.Triggers[0].Target).IsTypeOf<PollTargetDocument>();
    }
}

public class PipelineDocumentVersionTests
{
    [Test]
    [Arguments("0.0")]
    [Arguments("0.1")]
    [Arguments("0.99")]
    public async Task EnsureCompatible_SameMajor_Succeeds(string version)
    {
        await Assert.That(PipelineDocumentVersion.EnsureCompatible(version)).Succeeded();
    }

    [Test]
    [Arguments("1.0")]
    [Arguments("2.5")]
    public async Task EnsureCompatible_DifferentMajor_Fails(string version)
    {
        await Assert.That(PipelineDocumentVersion.EnsureCompatible(version)).Failed();
    }

    [Test]
    [Arguments("")]
    [Arguments("abc")]
    [Arguments("1")]
    [Arguments("1.2.3")]
    [Arguments("-1.0")]
    public async Task EnsureCompatible_Malformed_Fails(string version)
    {
        await Assert.That(PipelineDocumentVersion.EnsureCompatible(version)).Failed();
    }
}
