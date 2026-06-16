using System.ComponentModel;
using System.Diagnostics;

namespace Olve.Pipelines.Cli;

/// <summary>The captured outcome of running an external process to completion.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>stderr if present, otherwise stdout — whichever carries the diagnostic.</summary>
    public string Diagnostic => string.IsNullOrWhiteSpace(StandardError) ? StandardOutput.Trim() : StandardError.Trim();
}

/// <summary>
/// Runs external CLIs (kubectl, helm). Seam so commands are testable without a cluster.
/// A non-zero exit is still a successful <see cref="ProcessResult"/> (the process ran);
/// only a failure to launch (binary missing) is a <see cref="ResultProblem"/>.
/// </summary>
public interface IProcessRunner
{
    Task<Result<ProcessResult>> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? standardInput = null,
        CancellationToken ct = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<Result<ProcessResult>> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? standardInput = null,
        CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception ex)
        {
            return new ResultProblem(
                "Could not run '{0}': {1}. Is it installed and on PATH?", fileName, ex.Message);
        }

        if (process is null)
            return new ResultProblem("Could not start process '{0}'.", fileName);

        using (process)
        {
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput);
                process.StandardInput.Close();
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            return Result.Success(new ProcessResult(process.ExitCode, stdout, stderr));
        }
    }
}

/// <summary>Convenience helpers over <see cref="IProcessRunner"/>.</summary>
public static class ProcessRunnerExtensions
{
    /// <summary>
    /// Runs the process and treats a non-zero exit as a failure, folding the captured
    /// diagnostic into the problem so callers get one <see cref="Result"/> to check.
    /// </summary>
    public static async Task<Result<ProcessResult>> RunCheckedAsync(
        this IProcessRunner runner,
        string fileName,
        IReadOnlyList<string> arguments,
        string? standardInput = null,
        CancellationToken ct = default)
    {
        var result = await runner.RunAsync(fileName, arguments, standardInput, ct);
        if (result.TryPickProblems(out var problems, out var output))
            return problems;

        if (!output.Succeeded)
        {
            return new ResultProblem(
                "{0} {1} exited with code {2}: {3}",
                fileName, string.Join(' ', arguments), output.ExitCode, output.Diagnostic);
        }

        return output;
    }
}
