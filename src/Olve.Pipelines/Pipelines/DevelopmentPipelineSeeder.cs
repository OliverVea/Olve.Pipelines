using Olve.Pipelines.PipelineBuilders;
using Olve.Pipelines.PipelineSources;
using Olve.Pipelines.Processing;

namespace Olve.Pipelines.Pipelines;

public static class DevelopmentPipelineSeeder
{
    private static readonly Id<Pipeline> PipelineId = Id.FromName<Pipeline>("Olve.Pipelines");
    private static readonly Id<PipelineSource> SourceId = Id.FromName<PipelineSource>("Olve.Pipelines");
    private static readonly Id<PipelineBuilder> BuilderId = Id.FromName<PipelineBuilder>("Olve.Pipelines.Docker");
    private static readonly Id<ProcessingStep> ProcessingId = Id.FromName<ProcessingStep>("Olve.Pipelines.Deploy");
    private static readonly Id<Verification> VerificationId = Id.FromName<Verification>("Olve.Pipelines.Deploy.HealthCheck");

    public static IEnumerable<Pipeline> GetPipelines() =>
    [
        new Pipeline(PipelineId, "Olve.Pipelines"),
    ];

    public static IEnumerable<PipelineSource> GetPipelineSources() =>
    [
        new PipelineSource(SourceId, "Olve.Pipelines", PipelineId),
    ];

    public static IEnumerable<PipelineBuilder> GetPipelineBuilders() =>
    [
        new PipelineBuilder(BuilderId, "Docker", PipelineId),
    ];

    public static IEnumerable<ProcessingStep> GetProcessingSteps() =>
    [
        new ProcessingStep(ProcessingId, "Deploy", PipelineId),
    ];

    public static IEnumerable<Verification> GetVerifications() =>
    [
        new Verification(VerificationId, "Health Check", ProcessingId),
    ];

    public static void SeedImplementations(
        PipelineSourceService sources,
        PipelineBuilderService builders,
        ProcessingStepService processing,
        VerificationService verifications)
    {
        sources.SetGitHub(SourceId, new GitHubSource("OliverVea", "Olve.Pipelines", "main"));
        builders.SetScript(BuilderId, new ScriptBuilder("docker build -t olve-pipelines ."));
        processing.SetScript(ProcessingId, new ScriptProcessing("kubectl apply -f deploy.yaml"));
        verifications.SetScript(VerificationId, new ScriptVerification("curl -f http://localhost/health"));
    }
}
