namespace Olve.Pipelines.Cli.Commands;

/// <summary>
/// A single CLI command, addressed by a <see cref="Noun"/> and optional <see cref="Verb"/>
/// (e.g. <c>pipeline get</c>, or the verbless <c>install</c>). Commands declare their own
/// flags/aliases/arity explicitly — the dispatcher merges in the global flags and routes.
/// </summary>
public interface ICliCommand
{
    /// <summary>The first word of the command (e.g. <c>pipeline</c>, <c>install</c>).</summary>
    string Noun { get; }

    /// <summary>The second word, or <c>""</c> for a verbless command (e.g. <c>install</c>, <c>produce</c>).</summary>
    string Verb { get; }

    /// <summary>Long names of boolean switches this command accepts (without leading dashes).</summary>
    IReadOnlySet<string> BooleanFlags => CliCommand.NoFlags;

    /// <summary>Short-to-long alias map for this command's options.</summary>
    IReadOnlyDictionary<string, string> Aliases => CliCommand.NoAliases;

    /// <summary>Number of positional operands required after the noun/verb (e.g. an id).</summary>
    int RequiredOperands => 0;

    /// <summary>One-line summary shown in the noun's help listing.</summary>
    string HelpLine { get; }

    /// <summary>Optional detailed help (usage + options) shown for <c>--help</c> on this command.</summary>
    string? HelpDetail => null;

    Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct);
}

/// <summary>Shared empties so commands without flags/aliases stay terse.</summary>
public static class CliCommand
{
    public static readonly IReadOnlySet<string> NoFlags = new HashSet<string>(StringComparer.Ordinal);
    public static readonly IReadOnlyDictionary<string, string> NoAliases =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Registry key for a command: <c>"noun verb"</c>, or just <c>"noun"</c> when verbless.</summary>
    public static string Key(string noun, string verb) =>
        string.IsNullOrEmpty(verb) ? noun : $"{noun} {verb}";
}
