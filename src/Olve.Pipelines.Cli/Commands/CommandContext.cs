using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Diagnostics;
using Olve.Pipelines.Cli.Output;

namespace Olve.Pipelines.Cli.Commands;

/// <summary>
/// Per-invocation context handed to a command's <see cref="ICliCommand.Execute"/>: the parsed
/// global flags, the resolved API client, the output writer, the loaded config, and the stderr log.
/// </summary>
public sealed class CommandContext
{
    /// <summary>True when <c>--json</c> was passed: emit machine-readable output on stdout.</summary>
    public required bool Json { get; init; }

    /// <summary>True when <c>--verbose</c> was passed: emit diagnostic logging to stderr.</summary>
    public required bool Verbose { get; init; }

    /// <summary>The API client, configured with the resolved base URL + bearer token.</summary>
    public required IPipelinesApi Api { get; init; }

    /// <summary>Renders command results as JSON (<c>--json</c>) or human output.</summary>
    public required IOutputWriter Output { get; init; }

    /// <summary>Stderr diagnostic log (active under <c>--verbose</c>).</summary>
    public required IConsoleLog Log { get; init; }

    /// <summary>The config loaded from <c>~/.pl</c> (used by auth commands).</summary>
    public required CliConfig Config { get; init; }
}
