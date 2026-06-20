using System.Text.Json.Serialization;

namespace Olve.Pipelines.Cli.Api.Contracts;

public sealed class Trigger
{
    public Guid Id { get; set; }
    public Guid PipelineId { get; set; }
    public string Name { get; set; } = "";
    public TriggerTarget Target { get; set; } = new ProductionTriggerTarget();
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>What a trigger fires. Polymorphic on the server's <c>type</c> discriminator.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ProductionTriggerTarget), "production")]
[JsonDerivedType(typeof(ProcessingTriggerTarget), "processing")]
[JsonDerivedType(typeof(PollTriggerTarget), "poll")]
[JsonDerivedType(typeof(GitHubWebhookTarget), "github")]
public abstract class TriggerTarget
{
    [JsonIgnore] public abstract string Kind { get; }
}

public sealed class ProductionTriggerTarget : TriggerTarget
{
    [JsonIgnore] public override string Kind => "production";
}

public sealed class ProcessingTriggerTarget : TriggerTarget
{
    public Guid ProcessingStepId { get; set; }
    [JsonIgnore] public override string Kind => "processing";
}

public sealed class PollTriggerTarget : TriggerTarget
{
    public string Url { get; set; } = "";
    public string ValuePath { get; set; } = "";
    public int IntervalSeconds { get; set; }
    [JsonIgnore] public override string Kind => "poll";
}

public sealed class GitHubWebhookTarget : TriggerTarget
{
    [JsonIgnore] public override string Kind => "github";
}
