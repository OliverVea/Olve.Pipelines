using System.Text.Json.Serialization;

namespace Olve.Pipelines.Cli.Api.Contracts;

/// <summary>
/// A group of jobs created by a trigger (production fires N jobs, processing fires one).
/// Polymorphic on the server's <c>type</c> discriminator, mirroring <see cref="Job"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ProductionJobGroup), "production")]
[JsonDerivedType(typeof(ProcessingJobGroup), "processing")]
public abstract class JobGroup
{
    public Guid Id { get; set; }
    public Guid PipelineId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    [JsonIgnore] public abstract string Kind { get; }
}

public sealed class ProductionJobGroup : JobGroup
{
    public Guid ArtifactBundleId { get; set; }
    [JsonIgnore] public override string Kind => "production";
}

public sealed class ProcessingJobGroup : JobGroup
{
    public Guid ArtifactBundleId { get; set; }
    public Guid ProcessingStepId { get; set; }
    [JsonIgnore] public override string Kind => "processing";
}

/// <summary>Body for <c>PUT /api/processing-steps/{stepId}/promotion</c> — sets the gate.</summary>
public sealed class SetPromotionRequest
{
    public bool Blocked { get; set; }
}
