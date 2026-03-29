using Olve.Pipelines.PipelineArtifacts;
using Olve.Pipelines.PipelineBuilds;
using Olve.Pipelines.PipelineProcessing;
using Olve.Pipelines.PipelineSources;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.Pipelines;

public static class DevelopmentPipelineSeeder
{
    private static readonly Id<Pipeline> PipelineId = Id.FromName<Pipeline>("Olve.Pipelines");
    private static readonly Id<PipelineSource> SourceId = Id.FromName<PipelineSource>("Olve.Pipelines");
    private static readonly Id<PipelineBuild> BuildId = Id.FromName<PipelineBuild>("Olve.Pipelines.Docker");
    private static readonly Id<PipelineArtifact> ArtifactId = Id.FromName<PipelineArtifact>("Olve.Pipelines.Docker.Image");
    private static readonly Id<PipelineProcessingStep> ProcessingId = Id.FromName<PipelineProcessingStep>("Olve.Pipelines.Deploy");
    private static readonly Id<PipelineVerification> VerificationId = Id.FromName<PipelineVerification>("Olve.Pipelines.Deploy.HealthCheck");

    public static IEnumerable<Pipeline> GetPipelines() =>
    [
        new Pipeline(PipelineId, "Olve.Pipelines"),
    ];

    public static IEnumerable<PipelineSource> GetPipelineSources() =>
    [
        new GitHubRepositorySource(SourceId, "Olve.Pipelines", PipelineId, "OliverVea", "Olve.Pipelines", "main"),
    ];

    public static IEnumerable<PipelineBuild> GetPipelineBuilds() =>
    [
        new PipelineBuild(BuildId, "Docker", PipelineId),
    ];

    public static IEnumerable<PipelineArtifact> GetPipelineArtifacts() =>
    [
        new PipelineArtifact(ArtifactId, "Docker Image", BuildId),
    ];

    public static IEnumerable<PipelineProcessingStep> GetPipelineProcessing() =>
    [
        new PipelineProcessingStep(ProcessingId, "Deploy", PipelineId),
    ];

    public static IEnumerable<PipelineVerification> GetPipelineVerifications() =>
    [
        new PipelineVerification(VerificationId, "Health Check", ProcessingId),
    ];
}
