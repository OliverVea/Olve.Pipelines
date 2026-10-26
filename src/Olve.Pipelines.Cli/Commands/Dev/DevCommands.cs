using System.Text.Json;
using System.Text.Json.Nodes;

namespace Olve.Pipelines.Cli.Commands.Dev;

/// <summary>Shared plumbing for the <c>pl dev</c> commands.</summary>
public static class DevCommandSupport
{
    public const string DefaultDir = ".pipelines";

    private static readonly JsonSerializerOptions Indented = DevPool.IndentedJson;

    /// <summary>The absolute pipeline directory from <c>--dir</c> (default <c>.pipelines</c>); it must hold a pipeline.ts.</summary>
    public static Result<string> ResolveDir(CliArgs cli)
    {
        var dir = Path.GetFullPath(cli.Option("dir", DefaultDir));
        if (!File.Exists(Path.Combine(dir, "pipeline.ts")))
        {
            return new ResultProblem(
                "No pipeline.ts in '{0}'. Run `pl dev` from the repo root, or pass --dir <pipeline directory>.", dir);
        }

        return dir;
    }

    /// <summary>Resolves the dir and toolchain, describes the pipeline, and loads the local pool.</summary>
    public static async Task<Result<DevContext?>> OpenAsync(
        IProcessRunner runner, DevToolchain? toolchain, DevConsole console, CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if (ResolveDir(cli).TryPickProblems(out var dirProblems, out var dir))
            return dirProblems;

        if (toolchain is null)
        {
            if ((await DevToolchain.ResolveAsync(runner, dir, ct)).TryPickProblems(out var toolProblems, out var resolved))
                return toolProblems;
            toolchain = resolved;
        }

        var workspace = new DevWorkspace(Path.Combine(dir, ".olve"));
        var session = new DevSession(runner, toolchain, dir, workspace, console);

        if ((await session.DescribeAsync(ct)).TryPickProblems(out var describeProblems, out var described))
            return describeProblems;
        if (described.Manifest is null)
        {
            ReportDescribeFailure(console, described);
            ctx.ExitCode = described.ExitCode;
            return Result.Success<DevContext?>(null);
        }

        if (DevPool.Load(workspace.PoolJson).TryPickProblems(out var poolProblems, out var pool))
            return poolProblems;

        return new DevContext(session, described.Manifest, pool);
    }

    public static void ReportDescribeFailure(DevConsole console, DescribeOutcome outcome)
    {
        console.Err.WriteLine($"error: describe failed (exit {outcome.ExitCode}):");
        foreach (var line in outcome.Problems.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            console.Err.WriteLine($"  {line}");
    }

    /// <summary>Parses repeated <c>--secret NAME=value</c>.</summary>
    public static Result<Dictionary<string, string>> ParseSecrets(IReadOnlyList<string> values)
    {
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var eq = value.IndexOf('=');
            if (eq <= 0)
                return new ResultProblem("--secret expects NAME=value (the value is never echoed).");
            secrets[value[..eq]] = value[(eq + 1)..];
        }

        return secrets;
    }

    /// <summary>Parses <c>--from &lt;pipeline&gt;#&lt;run&gt;</c> (run is a run number).</summary>
    public static Result<(string Pipeline, int Run)> ParseFrom(string value)
    {
        var hash = value.LastIndexOf('#');
        if (hash <= 0 || hash == value.Length - 1 || !int.TryParse(value[(hash + 1)..], out var run) || run < 0)
            return new ResultProblem("--from expects <pipeline>#<run>, e.g. olve-pipelines#412 (got '{0}').", value);
        return (value[..hash], run);
    }

    /// <summary>Splits <c>--only a,b</c> (also repeatable).</summary>
    public static IReadOnlyList<string>? ParseList(IReadOnlyList<string> values)
    {
        var list = values
            .SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();
        return list.Count == 0 ? null : list;
    }

    /// <summary>Renders a step envelope: raw JSON under <c>--json</c>, a readable summary otherwise.</summary>
    public static void RenderEnvelope(DevConsole console, string step, StepRunOutcome outcome)
    {
        if (console.Json)
        {
            console.Out.WriteLine(outcome.Envelope?.ToJsonString(DevPool.CompactJson) ?? "null");
            return;
        }

        var envelope = outcome.Envelope;
        switch (outcome.Status)
        {
            case PoolEntry.Succeeded:
                console.Out.WriteLine($"{step}: succeeded");
                var output = envelope?["output"];
                console.Out.WriteLine(output is null ? "  (no output)" : Indent(output.ToJsonString(Indented)));
                break;
            case PoolEntry.Skipped:
                console.Out.WriteLine($"{step}: skipped: {DevManifest.Str(envelope?["reason"])}");
                break;
            case PoolEntry.Failed:
                var kind = DevManifest.Str(envelope?["error"]?["kind"]) ?? "handler";
                console.Out.WriteLine($"{step}: failed ({kind}, exit {outcome.ExitCode})");
                console.Out.WriteLine(Indent(DevManifest.Str(envelope?["error"]?["message"]) ?? ""));
                break;
            default:
                console.Out.WriteLine($"{step}: no result (exit {outcome.ExitCode}{ExitMeaning(outcome.ExitCode)}); see the log above");
                break;
        }
    }

    public static string ExitMeaning(int code) => code switch
    {
        64 => ": usage, unknown step",
        78 => ": pipeline invalid",
        130 or 143 => ": cancelled",
        _ => "",
    };

    private static string Indent(string text) =>
        string.Join('\n', text.Split('\n').Select(l => "  " + l));
}

/// <summary>A described pipeline with its session and local pool.</summary>
public sealed record DevContext(DevSession Session, DevManifest Manifest, DevPool Pool);

/// <summary><c>pl dev describe</c> — the manifest and describe-time checks, from source.</summary>
public sealed class DevDescribeCommand(IProcessRunner runner, DevToolchain? toolchain = null, DevConsole? console = null) : ICliCommand
{
    public IReadOnlySet<string> ValueOptions { get; } = CliCommand.Options("dir");
    public string Noun => "dev";
    public string Verb => "describe";
    public bool UsesApi => false;
    public string HelpLine => "Describe the local pipeline from source (manifest + checks)";

    public string? HelpDetail =>
        """
        pl dev describe [--dir <path>] [--json]

          Runs the SDK's describe on the pipeline source (Bun, no compile) and prints a summary of
          the manifest: input model, secrets, triggers, stages, `when` blocks, failure handlers.
          Describe-time problems are printed and the command exits 78.

          --dir <path>   Pipeline directory (default: .pipelines)
          --json         Print the raw manifest (canonical JSON) instead
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        var con = console ?? new DevConsole(ctx.Json);
        if ((await DevCommandSupport.OpenAsync(runner, toolchain, con, cli, ctx, ct)).TryPickProblems(out var problems, out var dev))
            return problems;
        if (dev is null)
            return Result.Success();

        if (con.Json)
            con.Out.Write(dev.Manifest.Text);
        else
            Render(con.Out, dev.Manifest);
        return Result.Success();
    }

    public static void Render(TextWriter o, DevManifest m)
    {
        o.WriteLine(m.Description is { Length: > 0 } d ? $"{m.Name} — {d}" : m.Name);
        o.WriteLine();
        if (m.InputModel is not null)
            o.WriteLine($"input:    {m.InputModel} {{ {string.Join(", ", InputFields(m.InputSchema))} }}");
        o.WriteLine($"secrets:  {(m.Secrets.Count == 0 ? "none" : string.Join(", ", m.Secrets.Select(s => s.Name)))}");
        o.WriteLine($"triggers: {(m.Triggers.Count == 0 ? "none (manual only)" : string.Join(", ", m.Triggers.Select(TriggerLabel)))}");
        o.WriteLine();
        o.WriteLine("stages:");
        var stage = 0;
        RenderNodes(m.Stages, "  ");
        if (m.FailureHandlers.Count > 0)
        {
            o.WriteLine();
            o.WriteLine("failure handlers:");
            foreach (var (step, scope) in m.FailureHandlers)
                o.WriteLine($"  {step.Name,-22} on {(scope is null ? "any step" : string.Join(", ", scope))}");
        }

        void RenderNodes(IReadOnlyList<DevStageNode> nodes, string indent)
        {
            foreach (var node in nodes)
            {
                switch (node)
                {
                    case DevStage s:
                        stage++;
                        var label = $"{stage}.";
                        foreach (var step in s.Steps)
                        {
                            o.WriteLine($"{indent}{label,-4}{StepLine(step)}");
                            label = "";
                        }

                        break;
                    case DevWhenBlock w:
                        o.WriteLine($"{indent}when {string.Join(" and ", w.Conditions)}:");
                        RenderNodes(w.Nodes, indent + "  ");
                        break;
                    case DevSelectBlock sel:
                        o.WriteLine($"{indent}select on config {sel.Select}.{sel.Discriminator}:");
                        var at = stage;
                        foreach (var (value, caseNodes) in sel.Cases)
                        {
                            stage = at;
                            o.WriteLine($"{indent}  {value}:");
                            RenderNodes(caseNodes, indent + "    ");
                        }
                        break;
                }
            }
        }
    }

    private static string StepLine(DevStep step)
    {
        var line = $"{step.Name,-20} {step.Image}";
        if (step.Output is not null)
            line += $" -> {step.Output}";
        if (step.Secrets.Count > 0)
            line += $"  [secrets: {string.Join(", ", step.Secrets)}]";
        if (step.ExternalInputs.Count > 0)
            line += $"  [waits for: {string.Join(", ", step.ExternalInputs.Select(e => $"{e.Key} ({e.Value})"))}]";
        return line;
    }

    private static IEnumerable<string> InputFields(JsonObject? schema)
    {
        var required = (schema?["required"] as JsonArray ?? []).Select(DevManifest.Str).ToHashSet();
        foreach (var (name, prop) in schema?["properties"] as JsonObject ?? [])
        {
            var field = required.Contains(name) ? $"{name}*" : name;
            if (prop?["default"] is { } def)
                field += $"={def.ToJsonString()}";
            if (prop?["enum"] is JsonArray members)
                field += $" ({string.Join("|", members.Select(v => DevManifest.Str(v) ?? v?.ToJsonString()))})";
            yield return field;
        }
    }

    private static string TriggerLabel(JsonObject t) => DevManifest.Str(t["kind"]) switch
    {
        "poll" => $"{DevManifest.Str(t["name"])} (GET {DevManifest.Str(t["url"])} every {t["intervalSeconds"]}s)",
        var kind => DevManifest.Str(t["name"]) ?? kind ?? "?",
    };
}

/// <summary><c>pl dev run &lt;step&gt;</c> — one step from source (or in its image with --docker).</summary>
public sealed class DevRunCommand(IProcessRunner runner, DevToolchain? toolchain = null, DevConsole? console = null) : ICliCommand
{
    public IReadOnlySet<string> ValueOptions { get; } = CliCommand.Options("dir", "from", "input", "input-file", "secret");
    public string Noun => "dev";
    public string Verb => "run";
    public bool UsesApi => false;
    public int RequiredOperands => 1;
    public IReadOnlySet<string> BooleanFlags { get; } = new HashSet<string>(StringComparer.Ordinal) { "docker" };
    public string HelpLine => "Run one step locally from source (or --docker in its image)";

    public string? HelpDetail =>
        """
        pl dev run <step> [options]

          Runs one step through the harness path (validate input -> handler -> validate output),
          from source with Bun, writing to <dir>/.olve/work/. Prints the envelope; the exit code is
          the harness's (0 ok/skipped, 1 handler threw, 65 input invalid, 66 output invalid, 78
          pipeline invalid).

          input.json is built from the step's manifest mappings against the local pool
          (<dir>/.olve/pool.json): `input.*` refs read the pipeline input (--input values over the
          pool's previous input, then the model's defaults); step refs read earlier steps' outputs
          from previous `pl dev run`/`pl dev pipeline` runs, so running steps in order chains them.
          Output files go to <dir>/.olve/files/ for later steps' ctx.files.fetch.

          --dir <path>             Pipeline directory (default: .pipelines)
          --input k=v              Set a pipeline-input field (repeatable; typed by the input model)
          --input-file <file>      Use this file as input.json verbatim (skips assembly)
          --secret NAME=value      Secret value (repeatable). Default: the step's declared secrets
                                   from the environment; other pipeline secrets are not passed on
          --docker                 Compile with olve-build and run the binary in the step's image
                                   like the pod (/olve layout, bundled loader, same env)
          --json                   Print the raw envelope on stdout (step output goes to stderr)
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        var stepName = cli.Operand(0)!;
        if (cli.Option("from") is { } from)
        {
            if (DevCommandSupport.ParseFrom(from).TryPickProblems(out var fromProblems, out var source))
                return fromProblems;
            return new ResultProblem(
                "--from {0}#{1}: pulling a run's input and outputs is not available until the v2 run API lands.",
                source.Pipeline, source.Run);
        }

        var inputFile = cli.Option("input-file");
        var inputPairs = cli.Options("input");
        if (inputFile is not null && inputPairs.Count > 0)
            return new ResultProblem("Use either --input-file or --input, not both.");
        if (inputFile is not null && !File.Exists(inputFile))
            return new ResultProblem("Input file '{0}' not found.", inputFile);
        if (DevCommandSupport.ParseSecrets(cli.Options("secret")).TryPickProblems(out var secretProblems, out var secrets))
            return secretProblems;

        var con = console ?? new DevConsole(ctx.Json);
        if ((await DevCommandSupport.OpenAsync(runner, toolchain, con, cli, ctx, ct)).TryPickProblems(out var problems, out var dev))
            return problems;
        if (dev is null)
            return Result.Success();

        var options = new DevRunOptions { SecretOverrides = secrets, Docker = cli.HasFlag("docker") };
        if ((await RunAsync(dev, stepName, inputFile, inputPairs, options, ct)).TryPickProblems(out var runProblems, out var outcome))
            return runProblems;

        DevCommandSupport.RenderEnvelope(con, stepName, outcome);
        ctx.ExitCode = outcome.ExitCode;
        return Result.Success();
    }

    /// <summary>Prepares input.json (from a file, or assembled from the pool), runs the step and saves the pool.</summary>
    public static async Task<Result<StepRunOutcome>> RunAsync(
        DevContext dev, string stepName, string? inputFile, IReadOnlyList<string> inputPairs, DevRunOptions options, CancellationToken ct)
    {
        var (session, manifest, pool) = dev;
        if (manifest.FindStep(stepName) is not { } step)
        {
            return new ResultProblem("Unknown step '{0}' (steps: {1}).",
                stepName, string.Join(", ", manifest.AllSteps().Select(s => s.Name)));
        }

        if (PipelineInput.Build(manifest.InputSchema, pool.Input, inputPairs).TryPickProblems(out var inputProblems, out var pipelineInput))
            return inputProblems;
        pool.Input = pipelineInput;

        if (inputFile is not null)
        {
            session.CopyInput(inputFile);
        }
        else
        {
            if (step.IsFailureHandler)
                return new ResultProblem("'{0}' is a failure handler: pass its FailureContext with --input-file.", step.Name);

            var assembly = StepInputAssembler.Assemble(step, pool);
            if (assembly.Problems.Count > 0)
                return new ResultProblemCollection(assembly.Problems.Select(p => new ResultProblem("{0}: {1}", step.Name, p)));
            if (assembly.SkippedBecause.Count > 0)
            {
                return new ResultProblem("{0} {1}; it would be skipped in a run (rerun that step, or pass --input-file).",
                    step.Name, string.Join("; ", assembly.SkippedBecause));
            }

            session.WriteInput(assembly.Input);
        }

        var run = await session.RunStepAsync(manifest, step, pool, options, ct);
        pool.Save(session.Workspace.PoolJson);
        return run;
    }
}

/// <summary>The outcome of one step in <c>pl dev pipeline</c>.</summary>
public sealed record PipelineStepResult(string Step, string Status, string? Detail);

/// <summary><c>pl dev pipeline</c> — run the stages locally in order, chaining the local pool.</summary>
public sealed class DevPipelineCommand(
    IProcessRunner runner, IConfirmPrompt prompt, DevToolchain? toolchain = null, DevConsole? console = null) : ICliCommand
{
    public IReadOnlySet<string> ValueOptions { get; } = CliCommand.Options("dir", "input", "only", "secret", "until");
    public string Noun => "dev";
    public string Verb => "pipeline";
    public bool UsesApi => false;
    public IReadOnlySet<string> BooleanFlags { get; } = new HashSet<string>(StringComparer.Ordinal) { "yes", "docker" };
    public IReadOnlyDictionary<string, string> Aliases { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["y"] = "yes" };
    public string HelpLine => "Run the pipeline's stages locally from source";

    public string? HelpDetail =>
        """
        pl dev pipeline [options]

          Runs the stages in order from source (steps of a stage run one after another locally),
          chaining outputs through the local pool and evaluating `when` blocks against the
          pipeline input. A step whose `when` does not match is skipped, and so is any later step
          that requires its output. A failed step stops the run (exit = the harness exit code).
          Before each step that declares secrets (they do real things) it asks for confirmation;
          without a terminal it refuses unless --yes. Failure handlers are not run.

          --dir <path>        Pipeline directory (default: .pipelines)
          --input k=v         Pipeline-input field (repeatable); the model's defaults fill the rest
          --until <step>      Stop after the stage containing <step>
          --only <a,b,...>    Run only these steps, reusing the pool's input and outputs for the rest
          --secret NAME=value Secret value (repeatable); default: from the environment
          --docker            Run every step compiled, in its image (see `pl dev run --help`)
          --yes, -y           Don't ask before steps that declare secrets
          --json              Print a JSON summary [{step, status, detail}] on stdout
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if (DevCommandSupport.ParseSecrets(cli.Options("secret")).TryPickProblems(out var secretProblems, out var secrets))
            return secretProblems;

        var con = console ?? new DevConsole(ctx.Json);
        if ((await DevCommandSupport.OpenAsync(runner, toolchain, con, cli, ctx, ct)).TryPickProblems(out var problems, out var dev))
            return problems;
        if (dev is null)
            return Result.Success();

        var options = new DevPipelineOptions
        {
            InputPairs = cli.Options("input"),
            Until = cli.Option("until"),
            Only = DevCommandSupport.ParseList(cli.Options("only")),
            Yes = cli.HasFlag("yes"),
            Run = new DevRunOptions { SecretOverrides = secrets, Docker = cli.HasFlag("docker"), WarnUndeclaredSecrets = false },
        };
        foreach (var name in secrets.Keys.Where(k => dev.Manifest.Secrets.All(s => s.Name != k)))
            con.Status($"warning: --secret {name} ignored: no step of the pipeline declares it");

        var results = new List<PipelineStepResult>();
        var run = await RunAsync(dev, options, prompt, con, results, ct);
        RenderSummary(con, results);
        if (run.TryPickProblems(out var runProblems, out var exitCode))
            return runProblems;
        ctx.ExitCode = exitCode;
        return Result.Success();
    }

    /// <summary>
    /// Runs the selected steps; appends one <see cref="PipelineStepResult"/> per step visited.
    /// Returns the exit code (0, or the failing step's harness code) or problems.
    /// </summary>
    public static async Task<Result<int>> RunAsync(
        DevContext dev, DevPipelineOptions options, IConfirmPrompt prompt, DevConsole console,
        List<PipelineStepResult> results, CancellationToken ct)
    {
        var (session, manifest, pool) = dev;
        var workspace = session.Workspace;

        if (DevPlan.Select(DevPlan.Flatten(manifest), options.Until, options.Only).TryPickProblems(out var planProblems, out var plan))
            return planProblems;

        // A full run starts a fresh pool; --only reuses it (input and the other steps' outputs).
        var partial = options.Only is { Count: > 0 };
        if (!partial)
        {
            pool.Input = null;
            pool.Steps.Clear();
        }

        if (PipelineInput.Build(manifest.InputSchema, pool.Input, options.InputPairs).TryPickProblems(out var inputProblems, out var input))
            return inputProblems;
        if (PipelineInput.MissingRequired(manifest.InputOutputSchema ?? manifest.InputSchema, input) is { Count: > 0 } missing)
        {
            return new ResultProblem("Pipeline input is missing required field(s): {0}. Pass {1}.",
                string.Join(", ", missing), string.Join(" ", missing.Select(m => $"--input {m}=…")));
        }

        pool.Input = input;
        pool.Save(workspace.PoolJson);

        foreach (var planned in plan)
        {
            var step = planned.Step;
            if (DevPlan.WhenMismatch(planned.Conditions, pool.Input) is { } whenReason)
            {
                Skip(step.Name, whenReason);
                continue;
            }

            var assembly = StepInputAssembler.Assemble(step, pool);
            if (assembly.Problems.Count > 0)
                return new ResultProblemCollection(assembly.Problems.Select(p => new ResultProblem("{0}: {1}", step.Name, p)));
            if (assembly.SkippedBecause.Count > 0)
            {
                Skip(step.Name, string.Join("; ", assembly.SkippedBecause));
                continue;
            }

            if (step.Secrets.Count > 0 && !options.Yes)
            {
                var answer = prompt.Confirm(
                    $"'{step.Name}' declares secrets ({string.Join(", ", step.Secrets)}) and may do real things. Run it?");
                if (answer == ConfirmAnswer.NotInteractive)
                {
                    return new ResultProblem(
                        "'{0}' declares secrets and no terminal is available to confirm; pass --yes to run it.", step.Name);
                }

                if (answer == ConfirmAnswer.No)
                {
                    results.Add(new PipelineStepResult(step.Name, "declined", "not confirmed; stopping"));
                    return new ResultProblem("Stopped before '{0}' (not confirmed).", step.Name);
                }
            }

            session.WriteInput(assembly.Input);
            var outcome = await session.RunStepAsync(manifest, step, pool, options.Run, ct);
            pool.Save(workspace.PoolJson);
            if (outcome.TryPickProblems(out var runProblems, out var result))
                return runProblems;

            if (!console.Json)
                DevCommandSupport.RenderEnvelope(console, step.Name, result);
            var entry = pool.Steps[step.Name];
            results.Add(new PipelineStepResult(step.Name, entry.Status, entry.Reason));
            if (!entry.IsSucceeded && entry.Status != PoolEntry.Skipped)
            {
                console.Status($"'{step.Name}' failed (exit {result.ExitCode}{DevCommandSupport.ExitMeaning(result.ExitCode)}); stopping");
                return result.ExitCode == 0 ? 1 : result.ExitCode;
            }

            if (result.ExitCode != 0)
                return result.ExitCode;
        }

        return 0;

        void Skip(string name, string reason)
        {
            console.Status($"skipping '{name}': {reason}");
            pool.RecordSkipped(name, reason, workspace);
            pool.Save(workspace.PoolJson);
            results.Add(new PipelineStepResult(name, PoolEntry.Skipped, reason));
        }
    }

    private static void RenderSummary(DevConsole console, IReadOnlyList<PipelineStepResult> results)
    {
        if (console.Json)
        {
            var array = new JsonArray();
            foreach (var r in results)
                array.Add((JsonNode)new JsonObject { ["step"] = r.Step, ["status"] = r.Status, ["detail"] = r.Detail });
            console.Out.WriteLine(array.ToJsonString(DevPool.CompactJson));
            return;
        }

        if (results.Count == 0)
            return;
        console.Out.WriteLine();
        console.Out.WriteLine("result:");
        var width = results.Max(r => r.Step.Length) + 2;
        foreach (var r in results)
            console.Out.WriteLine($"  {r.Step.PadRight(width)}{r.Status,-10}{r.Detail}".TrimEnd());
    }
}

/// <summary>Options for <c>pl dev pipeline</c>.</summary>
public sealed record DevPipelineOptions
{
    public IReadOnlyList<string> InputPairs { get; init; } = [];
    public string? Until { get; init; }
    public IReadOnlyList<string>? Only { get; init; }
    public bool Yes { get; init; }
    public DevRunOptions Run { get; init; } = new();
}
