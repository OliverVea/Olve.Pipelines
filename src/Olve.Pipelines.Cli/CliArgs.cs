namespace Olve.Pipelines.Cli;

/// <summary>
/// Minimal, AOT-safe argument parser (no reflection-based command frameworks).
/// Tokens are either positionals (e.g. <c>pipeline get &lt;id&gt;</c>) or <c>--key value</c> /
/// <c>--key=value</c> options or boolean <c>--flag</c> switches (declared in
/// <c>booleanFlags</c>). Short aliases are mapped by the caller before parsing.
/// </summary>
/// <remarks>
/// The dispatcher routes on the leading positionals (noun + optional verb), then sets
/// <see cref="OperandOffset"/> so commands read their own arguments via <see cref="Operand"/>
/// without caring how many leading tokens named the command.
/// </remarks>
public sealed class CliArgs
{
    private readonly Dictionary<string, string> _options;
    private readonly HashSet<string> _flags;
    private readonly List<string> _positionals;

    /// <summary>All positional tokens in order, including the command's noun/verb.</summary>
    public IReadOnlyList<string> Positionals => _positionals;

    /// <summary>Back-compat: the first positional (the noun for noun-verb commands).</summary>
    public string? Command => _positionals.Count > 0 ? _positionals[0] : null;

    /// <summary>Number of leading positionals that named the command (1 for verbless, 2 for noun-verb). Set by the dispatcher after routing.</summary>
    public int OperandOffset { get; set; }

    private CliArgs(List<string> positionals, Dictionary<string, string> options, HashSet<string> flags)
    {
        _positionals = positionals;
        _options = options;
        _flags = flags;
    }

    public string? Option(string name) => _options.GetValueOrDefault(name);

    public string Option(string name, string fallback) => _options.GetValueOrDefault(name) ?? fallback;

    public bool HasFlag(string name) => _flags.Contains(name);

    /// <summary>The positional at <paramref name="index"/> (absolute, including command tokens), or null.</summary>
    public string? Positional(int index) =>
        index >= 0 && index < _positionals.Count ? _positionals[index] : null;

    /// <summary>The command operand at <paramref name="index"/> (after the noun/verb tokens), or null.</summary>
    public string? Operand(int index) => Positional(OperandOffset + index);

    /// <summary>Count of operands after the command tokens.</summary>
    public int OperandCount => Math.Max(0, _positionals.Count - OperandOffset);

    /// <summary>
    /// Parses <paramref name="args"/>. <paramref name="booleanFlags"/> are the long names
    /// (without leading dashes) that take no value. <paramref name="aliases"/> maps short
    /// forms (e.g. <c>n</c>) to long names (e.g. <c>namespace</c>).
    /// </summary>
    public static Result<CliArgs> Parse(
        IReadOnlyList<string> args,
        IReadOnlySet<string> booleanFlags,
        IReadOnlyDictionary<string, string> aliases)
    {
        var positionals = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];

            if (!token.StartsWith('-'))
            {
                positionals.Add(token);
                continue;
            }

            var name = token.TrimStart('-');
            string? inlineValue = null;

            var eq = name.IndexOf('=');
            if (eq >= 0)
            {
                inlineValue = name[(eq + 1)..];
                name = name[..eq];
            }

            if (aliases.TryGetValue(name, out var canonical))
                name = canonical;

            if (booleanFlags.Contains(name))
            {
                if (inlineValue is not null)
                    return new ResultProblem("Flag '--{0}' does not take a value.", name);

                flags.Add(name);
                continue;
            }

            if (inlineValue is not null)
            {
                options[name] = inlineValue;
                continue;
            }

            if (i + 1 >= args.Count)
                return new ResultProblem("Option '--{0}' requires a value.", name);

            options[name] = args[++i];
        }

        return Result.Success(new CliArgs(positionals, options, flags));
    }
}
