using Olve.Pipelines.PipelineArtifacts;
using Olve.Pipelines.PipelineBuilders;
using Olve.Pipelines.PipelineSources;
using Olve.Pipelines.Processing;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.Pipelines;

public static class DevelopmentPipelineSeeder
{
    private static readonly Id<Pipeline> PipelineId = Id.FromName<Pipeline>("Olve.Pipelines");
    private static readonly Id<PipelineSource> SourceId = Id.FromName<PipelineSource>("Olve.Pipelines");
    private static readonly Id<PipelineBuilder> BuilderId = Id.FromName<PipelineBuilder>("Olve.Pipelines.Docker");
    private static readonly Id<PipelineArtifact> ArtifactId = Id.FromName<PipelineArtifact>("Olve.Pipelines.Docker.Image");
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

    public static IEnumerable<PipelineArtifact> GetPipelineArtifacts() =>
    [
        new PipelineArtifact(ArtifactId, "Docker Image", BuilderId),
    ];

    public static IEnumerable<ProcessingStep> GetProcessingSteps() =>
    [
        new ProcessingStep(ProcessingId, "Deploy", PipelineId),
    ];

    public static IEnumerable<Verification> GetVerifications() =>
    [
        new Verification(VerificationId, "Health Check", ProcessingId),
    ];
}
