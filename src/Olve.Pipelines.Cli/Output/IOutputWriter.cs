using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Olve.Pipelines.Cli.Output;

/// <summary>
/// Renders command results. With <c>--json</c> the DTO is serialized verbatim to stdout (one
/// object/array per invocation, jq-friendly); otherwise the command's human formatter runs.
/// Commands never touch <see cref="Console"/> directly so <c>--json</c> stdout stays clean.
/// </summary>
public interface IOutputWriter
{
    bool IsJson { get; }

    /// <summary>Emit <paramref name="value"/> as JSON (machine mode) or invoke <paramref name="human"/>.</summary>
    void Emit<T>(T value, JsonTypeInfo<T> typeInfo, Action human);

    /// <summary>Write a human-output line to stdout (no-op semantics still apply in JSON mode — avoid in formatters).</summary>
    void Line(string text = "");
}

/// <summary>Default writer over <see cref="Console.Out"/>; the <see cref="System.IO.TextWriter"/> is injectable for tests.</summary>
public sealed class ConsoleOutputWriter(bool json, TextWriter? stdout = null) : IOutputWriter
{
    private readonly TextWriter _out = stdout ?? Console.Out;

    public bool IsJson => json;

    public void Emit<T>(T value, JsonTypeInfo<T> typeInfo, Action human)
    {
        if (json)
            _out.WriteLine(JsonSerializer.Serialize(value, typeInfo));
        else
            human();
    }

    public void Line(string text = "") => _out.WriteLine(text);
}
