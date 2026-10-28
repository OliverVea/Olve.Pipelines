namespace Olve.Pipelines.Cli.Api.Contracts;

/// <summary>One saved config document (<c>config #Number</c>).</summary>
public sealed class ConfigSnapshotInfo
{
    public int Number { get; set; }
    public string Author { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public int? RestoredFrom { get; set; }
}

/// <summary><c>GET /api/pipelines/{id}/config</c>: the YAML as written (empty before the first save).</summary>
public sealed class ConfigDocument
{
    public ConfigSnapshotInfo? Snapshot { get; set; }
    public string Yaml { get; set; } = "";
}

public sealed class ChangedDefault
{
    public string Path { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

/// <summary>The config document checked against one version.</summary>
public sealed class ConfigCheckResponse
{
    public int Version { get; set; }
    public bool Valid { get; set; }
    public bool Satisfied { get; set; }
    public string[] Problems { get; set; } = [];
    public string[] ReferencedSecrets { get; set; } = [];
    public string[] MissingSecrets { get; set; } = [];
    public ChangedDefault[] ChangedDefaults { get; set; } = [];
    public string? Effective { get; set; }
    public string[] Unmet { get; set; } = [];
}

public sealed class ConfigSaveResponse
{
    public ConfigSnapshotInfo Snapshot { get; set; } = new();
    public ConfigCheckResponse? Active { get; set; }
    public ConfigCheckResponse? Pending { get; set; }
    public int? ActivatedVersion { get; set; }
}

public sealed class SetConfigRequest
{
    public required string Yaml { get; init; }
}

public sealed class RollbackConfigRequest
{
    public required int Snapshot { get; init; }
}

/// <summary>A version waiting for its config or secrets; <see cref="Held"/> = it is the active version, and new runs wait.</summary>
public sealed class PendingVersionInfo
{
    public int Version { get; set; }
    public DateTimeOffset Since { get; set; }
    public bool Held { get; set; }
    public string[] Reasons { get; set; } = [];
    public ChangedDefault[] ChangedDefaults { get; set; } = [];

    /// <summary>One line: <c>pending v9: config config.auth.clientId: is required; secret X not set</c>.</summary>
    public string Describe() =>
        $"{(Held ? "held" : "pending")} v{Version}: {string.Join("; ", Reasons)}"
        + (ChangedDefaults.Length == 0 ? "" : "; " + string.Join("; ", ChangedDefaults.Select(c => $"changes the default of {c.Path} ({c.From} → {c.To})")));
}
