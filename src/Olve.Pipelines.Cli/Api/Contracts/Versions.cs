using System.Text.Json.Serialization;

namespace Olve.Pipelines.Cli.Api.Contracts;

/// <summary>
/// A pipeline's activation (spec §6.2), flattened: <see cref="Type"/> is <c>active</c> (with
/// <see cref="VersionNumber"/>) or <c>draining</c> (from → to). Modeled as one class rather than a
/// polymorphic hierarchy so an unknown discriminator still deserializes.
/// </summary>
public sealed class PipelineActivation
{
    public string Type { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? VersionNumber { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? FromVersionNumber { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? ToVersionNumber { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? CancelRunning { get; set; }
    public DateTimeOffset? Since { get; set; }

    /// <summary>The version runs start on: the active one, or the switch target while draining.</summary>
    [JsonIgnore] public int? CurrentVersion => Type == "draining" ? ToVersionNumber : VersionNumber;

    public override string ToString() => Type switch
    {
        "active" => $"active v{VersionNumber}",
        "draining" => $"draining v{FromVersionNumber} -> v{ToVersionNumber}{(CancelRunning == true ? " (cancelling running)" : "")}",
        _ => Type,
    };
}

public sealed class PipelineVersionsResponse
{
    public PipelineActivation? Activation { get; set; }
    public PipelineVersionSummary[] Versions { get; set; } = [];
}

public sealed class PipelineVersionSummary
{
    public Guid Id { get; set; }
    public int Number { get; set; }
    public string ManifestHash { get; set; } = "";
    public string? CommitSha { get; set; }
    public Dictionary<string, int> StepVersions { get; set; } = [];
    public Dictionary<string, int> ModelVersions { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One pipeline version in full; <see cref="Manifest"/> is the canonical manifest JSON text.</summary>
public sealed class PipelineVersion
{
    public Guid Id { get; set; }
    public Guid PipelineId { get; set; }
    public int Number { get; set; }
    public string Manifest { get; set; } = "";
    public string ManifestHash { get; set; } = "";
    public string? CommitSha { get; set; }
    public Dictionary<string, int> StepVersions { get; set; } = [];
    public Dictionary<string, int> ModelVersions { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SwitchVersionRequest
{
    public required int Version { get; init; }
    public bool CancelRunning { get; init; }
}

/// <summary>Run-based pipeline health (<c>GetPipelineHealth</c>).</summary>
public sealed class PipelineHealth
{
    public string Status { get; set; } = "";
    public RunSummary? LastRun { get; set; }
    public RunSummary? LastSettledRun { get; set; }
    public int RunningRuns { get; set; }
    public PipelineActivation? Activation { get; set; }
    public bool Draining { get; set; }
    public string[] BlockedSteps { get; set; } = [];
    public RunSummary[] HeldRuns { get; set; } = [];
    public PendingVersionInfo? Pending { get; set; }
}

/// <summary><c>POST /api/manifests/validate</c> verdict (always 200).</summary>
public sealed class ManifestValidationResponse
{
    public bool Valid { get; set; }
    public string? ManifestHash { get; set; }
    public ManifestIssueView[] Errors { get; set; } = [];
    public ManifestIssueView[] Warnings { get; set; } = [];
}

public sealed class ManifestIssueView
{
    public string Code { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Message { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Step { get; set; }
}
