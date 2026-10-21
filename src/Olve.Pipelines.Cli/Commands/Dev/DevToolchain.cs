using System.Text.Json;
using System.Text.Json.Nodes;

namespace Olve.Pipelines.Cli.Commands.Dev;

/// <summary>
/// Where <c>pl dev</c> finds Bun and the SDK's <c>olve-dev</c> / <c>olve-build</c> entry points.
/// The bins are invoked as <c>bun &lt;cli.ts&gt;</c> (their <c>#!/usr/bin/env bun</c> shebang would
/// need bun on PATH).
/// </summary>
public sealed record DevToolchain(string Bun, string DevCli, string BuildCli)
{
    public const string BunEnvVar = "OLVE_BUN";
    public const string SdkEnvVar = "OLVE_SDK";

    /// <summary>
    /// Resolves Bun (<c>$OLVE_BUN</c>, then PATH, then <c>~/.bun/bin/bun</c>) and the SDK the
    /// pipeline itself imports: <c>$OLVE_SDK</c>, else Bun's own resolution of
    /// <c>@olve/pipelines</c> from the pipeline directory (so a <c>file:../sdk</c> dependency
    /// runs the SDK source, the same copy <c>pipeline.ts</c> loads).
    /// </summary>
    public static async Task<Result<DevToolchain>> ResolveAsync(
        IProcessRunner runner, string pipelineDir, CancellationToken ct)
    {
        if (FindBun().TryPickProblems(out var bunProblems, out var bun))
            return bunProblems;

        string sdkRoot;
        if (Environment.GetEnvironmentVariable(SdkEnvVar) is { Length: > 0 } fromEnv)
        {
            sdkRoot = Path.GetFullPath(fromEnv);
        }
        else
        {
            var probe = await runner.RunAsync(new ProcessSpec(bun,
            [
                "-e",
                "process.stdout.write(Bun.resolveSync('@olve/pipelines', process.argv[1]))",
                pipelineDir.TrimEnd('/') + "/",
            ]), ct);
            if (probe.TryPickProblems(out var probeProblems, out var resolved))
                return probeProblems;
            if (!resolved.Succeeded || string.IsNullOrWhiteSpace(resolved.StandardOutput))
            {
                return new ResultProblem(
                    "Cannot resolve @olve/pipelines from '{0}' (run `bun install` there, or set ${1} to the SDK directory): {2}",
                    pipelineDir, SdkEnvVar, resolved.Diagnostic);
            }

            if (FindPackageRoot(resolved.StandardOutput.Trim()) is not { } root)
                return new ResultProblem("No package.json above '{0}'.", resolved.StandardOutput.Trim());
            sdkRoot = root;
        }

        var devCli = BinPath(sdkRoot, "olve-dev", "src/dev/cli.ts");
        var buildCli = BinPath(sdkRoot, "olve-build", "src/build/cli.ts");
        if (!File.Exists(devCli))
            return new ResultProblem("The SDK at '{0}' has no olve-dev ({1} missing).", sdkRoot, devCli);

        return new DevToolchain(bun, devCli, buildCli);
    }

    public static Result<string> FindBun()
    {
        if (Environment.GetEnvironmentVariable(BunEnvVar) is { Length: > 0 } fromEnv)
        {
            return File.Exists(fromEnv)
                ? fromEnv
                : new ResultProblem("${0} is set to '{1}', which does not exist.", BunEnvVar, fromEnv);
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0)
                continue;
            var candidate = Path.Combine(dir, "bun");
            if (File.Exists(candidate))
                return candidate;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var fallback = Path.Combine(home, ".bun", "bin", "bun");
        if (File.Exists(fallback))
            return fallback;

        return new ResultProblem(
            "Bun not found: `pl dev` runs pipelines with Bun. Install it (https://bun.sh), put it on PATH, or set ${0} to the bun binary.",
            BunEnvVar);
    }

    private static string? FindPackageRoot(string path)
    {
        var dir = Path.GetDirectoryName(path);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "package.json")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    /// <summary>The <c>bin</c> entry from the SDK's package.json, falling back to the known path.</summary>
    private static string BinPath(string sdkRoot, string bin, string fallback)
    {
        try
        {
            var package = JsonNode.Parse(File.ReadAllText(Path.Combine(sdkRoot, "package.json")));
            if (DevManifest.Str(package?["bin"]?[bin]) is { } relative)
                return Path.GetFullPath(Path.Combine(sdkRoot, relative));
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // fall through to the known layout
        }

        return Path.GetFullPath(Path.Combine(sdkRoot, fallback));
    }
}

/// <summary>Answer to a confirmation prompt.</summary>
public enum ConfirmAnswer
{
    Yes,
    No,

    /// <summary>stdin is not a terminal: nobody can answer, so the caller must not proceed.</summary>
    NotInteractive,
}

/// <summary>Asks the operator a yes/no question (seam for tests).</summary>
public interface IConfirmPrompt
{
    ConfirmAnswer Confirm(string question);
}

/// <summary>Prompts on stderr and reads stdin; refuses (rather than hangs) when stdin is redirected.</summary>
public sealed class ConsoleConfirmPrompt : IConfirmPrompt
{
    public ConfirmAnswer Confirm(string question)
    {
        if (Console.IsInputRedirected)
            return ConfirmAnswer.NotInteractive;

        Console.Error.Write($"{question} [y/N] ");
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        return answer is "y" or "yes" ? ConfirmAnswer.Yes : ConfirmAnswer.No;
    }
}
