namespace Olve.Pipelines.Cli.Api.Contracts;

/// <summary>
/// CLI-side DTOs. These mirror the server's JSON shapes but model only the fields the CLI renders,
/// so they stay AOT-friendly (registered in <see cref="CliJsonContext"/>) and free of the
/// reflection-based polymorphic converter a generated client would carry. Unknown JSON
/// properties are ignored by System.Text.Json, so partial coverage is safe.
/// </summary>
public sealed class Pipeline
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>One list-page card (<c>GET /api/pipelines/overview</c>).</summary>
public sealed class PipelineOverview
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Repo { get; set; }
    public PipelineHealth Health { get; set; } = new();
}
