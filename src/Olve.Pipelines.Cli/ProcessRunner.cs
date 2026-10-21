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
/// A process invocation with an environment overlay, a working directory and optional live
/// line streaming (used by <c>pl dev</c>, where handler logs must appear as they happen).
/// </summary>
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments)
{
    /// <summary>Environment overlay on top of the current process's: a null value removes the variable.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();

    public string? WorkingDirectory { get; init; }

    /// <summary>When set, stdout is streamed line by line here instead of being captured.</summary>
    public Action<string>? OnStdoutLine { get; init; }

    /// <summary>When set, stderr is streamed line by line here instead of being captured.</summary>
    public Action<string>? OnStderrLine { get; init; }
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

    /// <summary>
    /// Runs <paramref name="spec"/> to completion. On cancellation the child (which shares the
    /// terminal and so received the same Ctrl-C) gets a grace period to exit before it is killed.
    /// </summary>
    Task<Result<ProcessResult>> RunAsync(ProcessSpec spec, CancellationToken ct = default) =>
        Task.FromResult<Result<ProcessResult>>(
            new ResultProblem("This process runner cannot run '{0}' with a ProcessSpec.", spec.FileName));
}

public sealed partial class ProcessRunner : IProcessRunner
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

public sealed partial class ProcessRunner
{
    private static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(15);

    public async Task<Result<ProcessResult>> RunAsync(ProcessSpec spec, CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (spec.WorkingDirectory is not null)
            startInfo.WorkingDirectory = spec.WorkingDirectory;
        foreach (var argument in spec.Arguments)
            startInfo.ArgumentList.Add(argument);
        foreach (var (name, value) in spec.Environment)
        {
            if (value is null)
                startInfo.Environment.Remove(name);
            else
                startInfo.Environment[name] = value;
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception ex)
        {
            return new ResultProblem(
                "Could not run '{0}': {1}. Is it installed and on PATH?", spec.FileName, ex.Message);
        }

        if (process is null)
            return new ResultProblem("Could not start process '{0}'.", spec.FileName);

        using (process)
        {
            var stdoutTask = Pump(process.StandardOutput, spec.OnStdoutLine);
            var stderrTask = Pump(process.StandardError, spec.OnStderrLine);

            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                using var grace = new CancellationTokenSource(CancelGrace);
                try
                {
                    await process.WaitForExitAsync(grace.Token);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return Result.Success(new ProcessResult(process.ExitCode, stdout, stderr));
        }
    }

    /// <summary>Reads a stream to the end, either streaming each line to <paramref name="onLine"/> or capturing it.</summary>
    private static async Task<string> Pump(StreamReader reader, Action<string>? onLine)
    {
        if (onLine is null)
            return await reader.ReadToEndAsync(CancellationToken.None);

        while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
            onLine(line);
        return "";
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
