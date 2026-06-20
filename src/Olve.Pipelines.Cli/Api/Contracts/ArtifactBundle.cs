namespace Olve.Pipelines.Cli.Api.Contracts;

public sealed class ArtifactBundle
{
    public Guid Id { get; set; }
    public Guid PipelineId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public int Status { get; set; }
}
