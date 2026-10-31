using System.Diagnostics;

namespace Olve.Pipelines.Recorder.Capture;

/// <summary>Runs a tool the recorder needs. Failure is loud: a partial capture is never written.</summary>
internal static class Captures
{
    public static string Run(string file, IEnumerable<string> arguments, string? workingDirectory = null, string? input = null)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"could not start {file}");
        var error = process.StandardError.ReadToEndAsync();
        if (input is not null)
        {
            process.StandardInput.Write(input);
            process.StandardInput.Close();
        }
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{file} {string.Join(' ', start.ArgumentList)} exited {process.ExitCode}: {error.Result}");
        return output;
    }
}
