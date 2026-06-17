using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Shared;
using Olve.Results;

namespace Olve.Pipelines.UnitTests;

/// <summary>
/// Guards the committed <c>.pipelines-test/config.yaml</c> fixture that the integration suite binds
/// to: it must compile and reconcile to exactly the shape those tests assert (one production step
/// <c>build</c>, one processing step <c>deploy</c>, both on <c>alpine:latest</c>).
///
/// The integration spike (<c>GitOpsReconcileSpikeTests</c>) exercises the same fixture end-to-end
/// through real GitHub + a container, but that needs Docker + network and runs only when integration
/// tests are explicitly enabled. This test runs in-process in the normal unit suite, so a broken or
/// drifted fixture is caught in CI without Docker — it reads the real committed bytes (the same text
/// GitHub serves to the reconcile loop), not an inlined copy.
/// </summary>
public class PipelinesTestFixtureConfigTests
{
    private const string ExpectedProductionStep = "build";
    private const string ExpectedProcessingStep = "deploy";

    private static string ReadFixtureConfig()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException("Could not find repository root.");

        var path = Path.Combine(dir.FullName, ".pipelines-test", "config.yaml");
        return File.ReadAllText(path);
    }

    private static T Pick<T>(Result<T> result)
    {
        if (result.TryPickProblems(out var problems, out var value))
            throw new InvalidOperationException(string.Join("; ", problems.Select(p => p.Message)));
        return value!;
    }

    [Test]
    public async Task FixtureConfig_Compiles()
    {
        var compiler = new ManifestCompiler();

        var result = compiler.Compile(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ManifestCompiler.ConfigFileName] = ReadFixtureConfig(),
        });

        await Assert.That(result.Succeeded).IsTrue();
    }

    [Test]
    public async Task FixtureConfig_ReconcilesToExpectedShape()
    {
        var compiler = new ManifestCompiler();
        var manifest = Pick(compiler.Compile(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ManifestCompiler.ConfigFileName] = ReadFixtureConfig(),
        }));

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

        var pipeline = Pick(pipelines.Create("fixture"));

        var reconcile = reconciler.Reconcile(pipeline.Id, manifest);
        await Assert.That(reconcile.Succeeded).IsTrue();

        var prodSteps = Pick(production.GetByPipelineId(pipeline.Id));
        await Assert.That(prodSteps.Select(s => s.Name)).Contains(ExpectedProductionStep);
        await Assert.That(prodSteps.Length).IsEqualTo(1);
        var prodConfig = Pick(production.TryGetConfiguration(prodSteps[0].Id));
        await Assert.That(prodConfig.Image).IsEqualTo("alpine:latest");

        var procSteps = Pick(processing.GetByPipelineId(pipeline.Id));
        await Assert.That(procSteps.Select(s => s.Name)).Contains(ExpectedProcessingStep);
        await Assert.That(procSteps.Length).IsEqualTo(1);
    }
}
