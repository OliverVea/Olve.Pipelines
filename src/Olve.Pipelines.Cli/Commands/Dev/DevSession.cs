using System.Text.Json;
using System.Text.Json.Nodes;

namespace Olve.Pipelines.Cli.Commands.Dev;

/// <summary>Where <c>pl dev</c> writes: final output (stdout), live child output and status lines.</summary>
public sealed class DevConsole(bool json, TextWriter? stdout = null, TextWriter? stderr = null)
{
    public bool Json { get; } = json;
    public TextWriter Out { get; } = stdout ?? Console.Out;
    public TextWriter Err { get; } = stderr ?? Console.Error;

    /// <summary>A pl status line on stderr (never mixed into <c>--json</c> stdout).</summary>
    public void Status(string message) => Err.WriteLine($"[pl dev] {message}");

    /// <summary>The child's stdout: to stdout normally, to stderr under <c>--json</c> so stdout stays parseable.</summary>
    public void ChildStdout(string line) => (Json ? Err : Out).WriteLine(line);

    public void ChildStderr(string line) => Err.WriteLine(line);
}

/// <summary>The result of <c>olve-dev describe</c>: the manifest, or the exit code and describe problems.</summary>
public sealed record DescribeOutcome(DevManifest? Manifest, int ExitCode, string Problems);

/// <summary>A step's result: the envelope from <c>work/output.json</c> (null when the harness wrote none) and the harness exit code.</summary>
public sealed record StepRunOutcome(JsonObject? Envelope, int ExitCode)
{
    public string Status => DevManifest.Str(Envelope?["status"]) ?? "no result";
}

/// <summary>Options for one step run.</summary>
public sealed record DevRunOptions
{
    /// <summary><c>--secret NAME=value</c> overrides; otherwise declared secrets come from the process environment.</summary>
    public IReadOnlyDictionary<string, string> SecretOverrides { get; init; } = new Dictionary<string, string>();

    public bool Docker { get; init; }

    /// <summary>Warn about <c>--secret</c> names the step does not declare (off for whole-pipeline runs).</summary>
    public bool WarnUndeclaredSecrets { get; init; } = true;
}

/// <summary>
/// Runs a pipeline from source through the SDK dev runner (<c>olve-dev</c>), or compiled in the
/// step's image (<c>--docker</c>), against the local workspace and pool.
/// </summary>
public sealed class DevSession(
    IProcessRunner runner,
    DevToolchain tools,
    string pipelineDir,
    DevWorkspace workspace,
    DevConsole console)
{
    public const string HarnessBaseImage = "registry.ovea.pro/olve-pipelines-v2/harness-base:0c4808c152bf";
    public const string HarnessBaseEnvVar = "OLVE_HARNESS_BASE";
    public const string Loader = "/olve/lib/ld-linux-x86-64.so.2";
    public const string ResultBegin = "---OLVE-RESULT-BEGIN---";
    public const string ResultEnd = "---OLVE-RESULT-END---";

    /// <summary>Harness env vars that would make a local run talk to real storage; never passed on.</summary>
    private static readonly string[] PrivateEnv =
        ["OLVE_RESULT_URL", "OLVE_FILE_URLS", "OLVE_FILES_BASE_URL", "OLVE_FILES_TOKEN"];

    public DevWorkspace Workspace => workspace;

    public async Task<Result<DescribeOutcome>> DescribeAsync(CancellationToken ct)
    {
        var result = await runner.RunAsync(new ProcessSpec(tools.Bun, [tools.DevCli, "describe", pipelineDir]), ct);
        if (result.TryPickProblems(out var problems, out var describe))
            return problems;

        if (!describe.Succeeded)
            return new DescribeOutcome(null, describe.ExitCode, describe.Diagnostic);

        if (describe.StandardError.Trim() is { Length: > 0 } warnings)
            console.ChildStderr(warnings);

        if (DevManifest.Parse(describe.StandardOutput).TryPickProblems(out var parseProblems, out var manifest))
            return parseProblems;

        return new DescribeOutcome(manifest, 0, "");
    }

    /// <summary>Writes <paramref name="input"/> as the next run's <c>input.json</c>.</summary>
    public void WriteInput(JsonNode input)
    {
        Directory.CreateDirectory(workspace.Root);
        File.WriteAllText(workspace.InputJson, input.ToJsonString(DevPool.IndentedJson) + "\n");
    }

    /// <summary>Copies a user-supplied input file to the next run's <c>input.json</c>.</summary>
    public void CopyInput(string file)
    {
        Directory.CreateDirectory(workspace.Root);
        File.Copy(file, workspace.InputJson, overwrite: true);
    }

    /// <summary>
    /// Runs <paramref name="step"/> against the current <c>input.json</c> and records the outcome in
    /// <paramref name="pool"/> (which the caller saves).
    /// </summary>
    public async Task<Result<StepRunOutcome>> RunStepAsync(
        DevManifest manifest, DevStep step, DevPool pool, DevRunOptions options, CancellationToken ct)
    {
        var secrets = SecretEnvironment(manifest, step, options.SecretOverrides, options.WarnUndeclaredSecrets);
        Directory.CreateDirectory(workspace.PoolFiles);
        if (File.Exists(workspace.OutputJson))
            File.Delete(workspace.OutputJson); // a usage/config failure writes none; never read a stale one

        var run = options.Docker
            ? await RunInDockerAsync(manifest, step, secrets, ct)
            : await RunFromSourceAsync(manifest, step, secrets, ct);
        if (run.TryPickProblems(out var problems, out var exitCode))
            return problems;

        var envelope = ReadEnvelope();
        pool.Record(step.Name, envelope, workspace);
        return new StepRunOutcome(envelope, exitCode);
    }

    private async Task<Result<int>> RunFromSourceAsync(
        DevManifest manifest, DevStep step, Dictionary<string, string?> secrets, CancellationToken ct)
    {
        console.Status($"running '{step.Name}' from source");
        var env = HarnessEnvironment(manifest, workspace.Root, workspace.Tmp, workspace.PoolFiles);
        foreach (var (k, v) in secrets)
            env[k] = v;

        var result = await runner.RunAsync(
            new ProcessSpec(tools.Bun, [tools.DevCli, "run", pipelineDir, step.Name, "--olve-dir", workspace.Root])
            {
                Environment = env,
                OnStdoutLine = ResultFilter(),
                OnStderrLine = console.ChildStderr,
            },
            ct);
        if (result.TryPickProblems(out var problems, out var process))
            return problems;
        return process.ExitCode;
    }

    private async Task<Result<int>> RunInDockerAsync(
        DevManifest manifest, DevStep step, Dictionary<string, string?> secrets, CancellationToken ct)
    {
        if (await BuildAsync(ct) is { } buildProblem)
            return buildProblem;
        if ((await EnsureLoaderLibsAsync(ct)).TryPickProblems(out var libProblems, out var libDir))
            return libProblems;

        StageDockerRoot(libDir);

        // Same env layout as the pod; secrets are passed by name (`-e NAME`) so docker reads the
        // values from its own environment and they never appear on a command line.
        var args = new List<string> { "create", "--entrypoint", Loader };
        foreach (var (k, v) in HarnessEnvironment(manifest, "/olve", "/olve/tmp", "/olve-pool"))
        {
            if (v is not null)
                args.AddRange(["-e", $"{k}={v}"]);
        }

        var dockerEnv = new Dictionary<string, string?>(secrets);
        foreach (var name in step.Secrets)
        {
            if (secrets.ContainsKey(name) || Environment.GetEnvironmentVariable(name) is not null)
                args.AddRange(["-e", name]);
        }

        args.AddRange(["-v", $"{workspace.PoolFiles}:/olve-pool:ro"]);
        args.AddRange([step.Image, "--library-path", "/olve/lib", "/olve/step", "run", step.Name]);

        console.Status($"running '{step.Name}' in {step.Image}");
        var created = await runner.RunAsync(new ProcessSpec("docker", args) { Environment = dockerEnv, OnStderrLine = console.ChildStderr }, ct);
        if (created.TryPickProblems(out var createProblems, out var create))
            return createProblems;
        if (!create.Succeeded)
            return new ResultProblem("docker create failed (exit {0}).", create.ExitCode);

        var container = create.StandardOutput.Trim();
        try
        {
            // `docker cp` (rather than a bind mount) gives the step an emptyDir-like /olve and brings
            // work/ back owned by the caller, even when the image runs as root.
            var copyIn = await runner.RunAsync(new ProcessSpec("docker", ["cp", workspace.DockerStaging + "/.", $"{container}:/olve"]), ct);
            if (copyIn.TryPickProblems(out var cpProblems, out var cp))
                return cpProblems;
            if (!cp.Succeeded)
                return new ResultProblem("docker cp into the container failed: {0}", cp.Diagnostic);

            var started = await runner.RunAsync(
                new ProcessSpec("docker", ["start", "--attach", container])
                {
                    Environment = dockerEnv,
                    OnStdoutLine = ResultFilter(),
                    OnStderrLine = console.ChildStderr,
                },
                ct);
            if (started.TryPickProblems(out var startProblems, out var start))
                return startProblems;

            if (Directory.Exists(workspace.Work))
                Directory.Delete(workspace.Work, recursive: true);
            Directory.CreateDirectory(workspace.Work);
            var copyOut = await runner.RunAsync(new ProcessSpec("docker", ["cp", $"{container}:/olve/work/.", workspace.Work]), CancellationToken.None);
            if (copyOut.TryPickProblems(out var outProblems, out var outResult))
                return outProblems;
            if (!outResult.Succeeded)
                console.Status($"could not copy /olve/work out of the container: {outResult.Diagnostic}");

            return start.ExitCode;
        }
        finally
        {
            _ = await runner.RunAsync(new ProcessSpec("docker", ["rm", "--force", container]), CancellationToken.None);
        }
    }

    /// <summary>Compiles the pipeline with olve-build into <c>.olve/build</c>. Null on success.</summary>
    private async Task<ResultProblem?> BuildAsync(CancellationToken ct)
    {
        console.Status($"compiling with olve-build into {workspace.Build}");
        var result = await runner.RunAsync(
            new ProcessSpec(tools.Bun, [tools.BuildCli, pipelineDir, "--out", workspace.Build])
            {
                OnStdoutLine = console.ChildStderr,
                OnStderrLine = console.ChildStderr,
            },
            ct);
        if (result.TryPickProblems(out var problems, out var build))
            return problems.First();
        if (!build.Succeeded)
            return new ResultProblem("olve-build failed (exit {0}); see the output above.", build.ExitCode);
        if (!File.Exists(Path.Combine(workspace.Build, "step")))
            return new ResultProblem("olve-build produced no '{0}'.", Path.Combine(workspace.Build, "step"));
        return null;
    }

    /// <summary>
    /// The bundled glibc loader + libs, extracted once from the harness-base image (<c>/olve/lib</c>)
    /// and cached in <c>.olve/lib/&lt;tag&gt;/</c>.
    /// </summary>
    private async Task<Result<string>> EnsureLoaderLibsAsync(CancellationToken ct)
    {
        var image = Environment.GetEnvironmentVariable(HarnessBaseEnvVar) is { Length: > 0 } custom ? custom : HarnessBaseImage;
        var tag = image[(image.LastIndexOfAny([':', '@']) + 1)..];
        var libDir = workspace.LibCache(tag);
        if (File.Exists(Path.Combine(libDir, "ld-linux-x86-64.so.2")))
            return libDir;

        console.Status($"extracting the loader libs from {image}");
        var created = await runner.RunAsync(new ProcessSpec("docker", ["create", image]) { OnStderrLine = console.ChildStderr }, ct);
        if (created.TryPickProblems(out var problems, out var create))
            return problems;
        if (!create.Succeeded)
            return new ResultProblem("docker create {0} failed (exit {1}).", image, create.ExitCode);

        var container = create.StandardOutput.Trim();
        var partial = libDir + ".partial";
        try
        {
            if (Directory.Exists(partial))
                Directory.Delete(partial, recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
            var copied = await runner.RunAsync(new ProcessSpec("docker", ["cp", $"{container}:/olve/lib", partial]), ct);
            if (copied.TryPickProblems(out var cpProblems, out var cp))
                return cpProblems;
            if (!cp.Succeeded)
                return new ResultProblem("Could not copy /olve/lib out of {0}: {1}", image, cp.Diagnostic);

            if (Directory.Exists(libDir))
                Directory.Delete(libDir, recursive: true);
            Directory.Move(partial, libDir);
            return libDir;
        }
        finally
        {
            _ = await runner.RunAsync(new ProcessSpec("docker", ["rm", "--force", container]), CancellationToken.None);
        }
    }

    /// <summary>Lays out <c>.olve/docker/</c> like the pod's <c>/olve</c> emptyDir after olve-init.</summary>
    private void StageDockerRoot(string libDir)
    {
        var root = workspace.DockerStaging;
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);

        File.Copy(Path.Combine(workspace.Build, "step"), Path.Combine(root, "step"));
        DevPool.CopyDirectory(libDir, Path.Combine(root, "lib"));
        if (File.Exists(workspace.InputJson))
            File.Copy(workspace.InputJson, Path.Combine(root, "input.json"));

        const UnixFileMode all = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        const UnixFileMode rx = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(root, rx);
        File.SetUnixFileMode(Path.Combine(root, "step"), rx);
        // 1777 like olve-init, so images that run as non-root can write.
        foreach (var dir in new[] { "work", Path.Combine("work", "files"), "tmp" })
        {
            var path = Path.Combine(root, dir);
            Directory.CreateDirectory(path);
            File.SetUnixFileMode(path, all | UnixFileMode.StickyBit);
        }
    }

    /// <summary>The harness env for a local run (sdk README, "Environment"), with storage-facing variables removed.</summary>
    private static Dictionary<string, string?> HarnessEnvironment(DevManifest manifest, string olveDir, string tmp, string filesDir)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["OLVE_DIR"] = olveDir,
            ["TMPDIR"] = tmp,
            ["OLVE_FILES_DIR"] = filesDir,
            ["OLVE_PIPELINE"] = manifest.Name,
            ["OLVE_RUN_ID"] = "local",
            ["OLVE_RUN"] = DevPool.Run.ToString(),
            ["OLVE_ATTEMPT"] = DevPool.Attempt.ToString(),
            ["OLVE_VERSION_STAMP"] = "0.0.0-local",
        };
        foreach (var name in PrivateEnv)
            env[name] = null;
        return env;
    }

    /// <summary>
    /// Secret env overlay: only the step's declared secrets reach it (other pipeline secrets are
    /// removed from the child env), each from <c>--secret</c> or else the process environment.
    /// Missing or undeclared ones are reported as warnings; the harness throws only if the handler reads one.
    /// </summary>
    public Dictionary<string, string?> SecretEnvironment(
        DevManifest manifest, DevStep step, IReadOnlyDictionary<string, string> overrides, bool warnUndeclared = true)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (name, _) in manifest.Secrets)
        {
            if (!step.Secrets.Contains(name))
                env[name] = null;
        }

        foreach (var name in overrides.Keys.Where(k => warnUndeclared && !step.Secrets.Contains(k)))
            console.Status($"warning: --secret {name} ignored: '{step.Name}' does not declare it");

        var missing = new List<string>();
        foreach (var name in step.Secrets)
        {
            if (overrides.TryGetValue(name, out var value))
                env[name] = value;
            else if (Environment.GetEnvironmentVariable(name) is null)
                missing.Add(name);
        }

        if (missing.Count > 0)
        {
            console.Status(
                $"warning: '{step.Name}' declares secret(s) {string.Join(", ", missing)} but they are not set; "
                + "export them or pass --secret NAME=value (the step fails if it reads one)");
        }

        return env;
    }

    /// <summary>Streams child stdout, dropping the result sentinel block (the envelope is read from output.json).</summary>
    private Action<string> ResultFilter()
    {
        var inBlock = false;
        return line =>
        {
            if (line == ResultBegin)
            {
                inBlock = true;
                return;
            }

            if (line == ResultEnd)
            {
                inBlock = false;
                return;
            }

            if (!inBlock)
                console.ChildStdout(line);
        };
    }

    private JsonObject? ReadEnvelope()
    {
        if (!File.Exists(workspace.OutputJson))
            return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(workspace.OutputJson)) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
