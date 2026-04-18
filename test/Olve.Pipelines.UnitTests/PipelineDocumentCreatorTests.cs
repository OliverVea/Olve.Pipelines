using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Shared;
using Olve.Results.TUnit;

namespace Olve.Pipelines.UnitTests;

public class PipelineDocumentCreatorTests
{
    private sealed record Fixture(
        PipelineDocumentCreator Creator,
        EntityStore<Pipeline> Pipelines,
        EntityStore<ProductionStep> ProductionSteps,
        EntityStore<ProcessingStep> ProcessingSteps,
        EntityStore<Trigger> Triggers,
        AttachmentStore<ProductionStep, StepConfiguration> ProductionConfigs,
        AttachmentStore<ProcessingStep, StepConfiguration> ProcessingConfigs);

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
            pipelineService, productionService, processingService, triggerService,
            productionConfigs, processingConfigs, processingStore);
        var creator = new PipelineDocumentCreator(
            pipelineService, productionService, processingService, triggerService, builder);

        return new Fixture(creator, pipelineStore, productionStore, processingStore, triggerStore,
            productionConfigs, processingConfigs);
    }

    private static PipelineDocument MakeDocument(
        string name = "p",
        IReadOnlyList<ProductionStepDocument>? production = null,
        IReadOnlyList<ProcessingStepDocument>? processing = null,
        IReadOnlyList<TriggerDocument>? triggers = null)
        => new(
            PipelineDocumentVersion.Current,
            name,
            production ?? Array.Empty<ProductionStepDocument>(),
            processing ?? Array.Empty<ProcessingStepDocument>(),
            triggers ?? Array.Empty<TriggerDocument>());

    [Test]
    public async Task Create_MinimalDocument_CreatesPipeline()
    {
        var f = CreateFixture();
        var result = f.Creator.Create(MakeDocument(name: "minimal"));

        await Assert.That(result).Succeeded();
        await Assert.That(f.Pipelines.List().Count).IsEqualTo(1);
        result.TryPickProblems(out _, out var doc);
        await Assert.That(doc!.Name).IsEqualTo("minimal");
    }

    [Test]
    public async Task Create_WithProductionSteps_CreatesAllAndAttachesConfig()
    {
        var f = CreateFixture();
        var result = f.Creator.Create(MakeDocument(
            production: new[]
            {
                new ProductionStepDocument("build", new StepConfigurationDocument("img", "s", null)),
                new ProductionStepDocument("bare", null),
            }));

        await Assert.That(result).Succeeded();
        await Assert.That(f.ProductionSteps.List().Count).IsEqualTo(2);
        result.TryPickProblems(out _, out var doc);
        var byName = doc!.ProductionSteps.ToDictionary(s => s.Name);
        await Assert.That(byName["build"].Configuration!.Image).IsEqualTo("img");
        await Assert.That(byName["bare"].Configuration).IsNull();
    }

    [Test]
    public async Task Create_ProcessingSteps_OrderMatchesListIndex()
    {
        var f = CreateFixture();
        var result = f.Creator.Create(MakeDocument(
            processing: new[]
            {
                new ProcessingStepDocument("first", null),
                new ProcessingStepDocument("second", null),
                new ProcessingStepDocument("third", null),
            }));

        result.TryPickProblems(out _, out var doc);
        await Assert.That(doc!.ProcessingSteps.Select(s => s.Name).ToArray())
            .IsEquivalentTo(new[] { "first", "second", "third" });

        var byName = f.ProcessingSteps.List().ToDictionary(s => s.Name);
        await Assert.That(byName["first"].Order).IsEqualTo(0);
        await Assert.That(byName["second"].Order).IsEqualTo(1);
        await Assert.That(byName["third"].Order).IsEqualTo(2);
    }

    [Test]
    public async Task Create_TriggerReferencingProcessingStep_ResolvesByName()
    {
        var f = CreateFixture();
        var result = f.Creator.Create(MakeDocument(
            processing: new[] { new ProcessingStepDocument("deploy", null) },
            triggers: new[]
            {
                new TriggerDocument("after-build", new ProcessingTargetDocument("deploy")),
            }));

        result.TryPickProblems(out _, out var doc);
        var target = (ProcessingTargetDocument)doc!.Triggers[0].Target;
        await Assert.That(target.ProcessingStepName).IsEqualTo("deploy");
        await Assert.That(f.Triggers.List().Count).IsEqualTo(1);
    }

    [Test]
    public async Task Create_TriggerReferencingMissingStep_Fails_AndRollsBackWrites()
    {
        var f = CreateFixture();
        var result = f.Creator.Create(MakeDocument(
            processing: new[] { new ProcessingStepDocument("deploy", null) },
            triggers: new[]
            {
                new TriggerDocument("broken", new ProcessingTargetDocument("does-not-exist")),
            }));

        await Assert.That(result).Failed();
        await Assert.That(f.Pipelines.List().Count).IsEqualTo(0);
        await Assert.That(f.ProcessingSteps.List().Count).IsEqualTo(0);
        await Assert.That(f.Triggers.List().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Create_IncompatibleApiVersion_Fails()
    {
        var f = CreateFixture();
        var result = f.Creator.Create(new PipelineDocument(
            "1.0", "p",
            Array.Empty<ProductionStepDocument>(),
            Array.Empty<ProcessingStepDocument>(),
            Array.Empty<TriggerDocument>()));

        await Assert.That(result).Failed();
        await Assert.That(f.Pipelines.List().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Create_PollTrigger_PreservesFields()
    {
        var f = CreateFixture();
        var result = f.Creator.Create(MakeDocument(
            triggers: new[]
            {
                new TriggerDocument("poll", new PollTargetDocument(
                    "https://example.com/api",
                    new Dictionary<string, string> { ["Authorization"] = "Bearer $SECRET:TOKEN" },
                    "$.sha",
                    120)),
            }));

        result.TryPickProblems(out _, out var doc);
        var poll = (PollTargetDocument)doc!.Triggers[0].Target;
        await Assert.That(poll.Url).IsEqualTo("https://example.com/api");
        await Assert.That(poll.ValuePath).IsEqualTo("$.sha");
        await Assert.That(poll.IntervalSeconds).IsEqualTo(120);
        await Assert.That(poll.Headers!["Authorization"]).IsEqualTo("Bearer $SECRET:TOKEN");
    }

    [Test]
    public async Task Create_DuplicateNames_AllowedAcrossPipelines()
    {
        var f = CreateFixture();
        var doc = MakeDocument(name: "shared",
            production: new[] { new ProductionStepDocument("build", null) });

        var first = f.Creator.Create(doc);
        var second = f.Creator.Create(doc);

        await Assert.That(first).Succeeded();
        await Assert.That(second).Succeeded();
        await Assert.That(f.Pipelines.List().Count).IsEqualTo(2);
        await Assert.That(f.ProductionSteps.List().Count).IsEqualTo(2);
    }
}
