namespace Olve.Pipelines.Cli.Diagnostics;

/// <summary>Diagnostic logging to <b>stderr</b> (so stdout stays clean for piping/jq). Active only under <c>--verbose</c>.</summary>
public interface IConsoleLog
{
    void Log(string message);
}

public sealed class StderrLog(bool verbose, TextWriter? stderr = null) : IConsoleLog
{
    private readonly TextWriter _err = stderr ?? Console.Error;

    public void Log(string message)
    {
        if (verbose)
            _err.WriteLine($"[pl] {message}");
    }
}
