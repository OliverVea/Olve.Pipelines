namespace Olve.Pipelines.Cli.Api.Contracts;

/// <summary>
/// CLI-side DTOs. These mirror the server's JSON shapes but model only the fields the CLI renders,
/// so they stay AOT-friendly (registered in <see cref="CliJsonContext"/>) and free of the
/// reflection-based polymorphic converter the generated Refit client carries. Unknown JSON
/// properties are ignored by System.Text.Json, so partial coverage is safe.
/// </summary>
public sealed class Pipeline
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class PipelineSummary
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Repo { get; set; }
    public StepHealth[] Steps { get; set; } = [];
    public DateTimeOffset? LastChangedAt { get; set; }
}

public sealed class StepHealth
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTimeOffset? LastChangedAt { get; set; }
}
