using System.Text.Json.Nodes;
using Olve.Pipelines.Cli;
using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Commands;
using Olve.Pipelines.Cli.Commands.Dev;
using Olve.Pipelines.Cli.Diagnostics;
using Olve.Pipelines.Cli.Output;
using Olve.Results;

namespace Olve.Pipelines.UnitTests.Cli.Dev;

/// <summary>A small manifest exercising refs, consts, nesting, optional inputs, secrets and nested `when`.</summary>
internal static class DevFixtures
{
    public const string Sha = "0123456789abcdef0123456789abcdef01234567";

    public const string ManifestJson = """
    {
      "apiVersion": "2.0",
      "name": "demo",
      "description": "A demo pipeline.",
      "input": "Req",
      "models": {
        "Req": {
          "input": {
            "type": "object",
            "properties": {
              "sha": { "type": "string" },
              "deployTo": { "type": "string", "enum": ["beta", "prod"], "default": "beta" },
              "publish": { "type": "boolean", "default": false },
              "replicas": { "type": "integer", "default": 1 },
              "labels": { "type": "object" }
            },
            "required": ["sha"]
          },
          "output": {
            "type": "object",
            "additionalProperties": false,
            "properties": {
              "sha": { "type": "string" },
              "deployTo": { "type": "string", "enum": ["beta", "prod"], "default": "beta" },
              "publish": { "type": "boolean", "default": false },
              "replicas": { "type": "integer", "default": 1 },
              "labels": { "type": "object" }
            },
            "required": ["sha", "deployTo", "publish", "replicas"]
          }
        }
      },
      "secrets": [ { "name": "TOKEN", "description": "t" }, { "name": "SSH_KEY", "description": "s" } ],
      "triggers": [ { "kind": "push", "name": "push" } ],
      "stages": [
        { "steps": [ { "name": "src", "kind": "custom", "image": "alpine", "output": "Source", "secrets": ["TOKEN"],
                       "input": { "sha": { "ref": "input.sha" }, "opts": { "depth": { "const": 1 }, "target": { "ref": "input.deployTo" } } },
                       "inputTypes": { "sha": { "schema": { "output": { "type": "string" } } }, "opts": { "schema": { "output": { "type": "object" } } } } } ] },
        { "steps": [
            { "name": "build", "kind": "custom", "image": "alpine", "output": "Artifact", "secrets": [],
              "input": { "source": { "ref": "src" }, "commit": { "ref": "src.sha" } },
              "inputTypes": { "source": { "model": "Source", "optional": false }, "commit": { "schema": { "output": { "type": "string" } } } } },
            { "name": "lint", "kind": "custom", "image": "alpine", "output": null, "secrets": [],
              "input": { "source": { "ref": "src" } }, "inputTypes": { "source": { "model": "Source", "optional": false } } } ] },
        { "when": { "deployTo": ["prod"] }, "stages": [
            { "steps": [ { "name": "deploy", "kind": "custom", "image": "alpine", "output": "Release", "secrets": ["SSH_KEY"],
                           "input": { "artifact": { "ref": "build" } }, "inputTypes": { "artifact": { "model": "Artifact", "optional": false } } } ] },
            { "when": { "publish": [true] }, "stages": [
                { "steps": [ { "name": "publish", "kind": "custom", "image": "alpine", "output": null, "secrets": ["TOKEN"],
                               "input": { "release": { "ref": "deploy" } }, "inputTypes": { "release": { "model": "Release", "optional": false } },
                               "externalInputs": { "verdict": { "model": "Verdict", "title": "Accepted?", "description": "", "from": [ { "kind": "person" } ] } } } ] } ] } ] },
        { "steps": [ { "name": "summary", "kind": "custom", "image": "alpine", "output": null, "secrets": [],
                       "input": { "artifact": { "ref": "build" }, "release": { "ref": "deploy" } },
                       "inputTypes": { "artifact": { "model": "Artifact", "optional": false }, "release": { "model": "Release", "optional": true } } } ] },
        { "steps": [ { "name": "notify", "kind": "custom", "image": "alpine", "output": null, "secrets": [],
                       "input": { "release": { "ref": "deploy" } }, "inputTypes": { "release": { "model": "Release", "optional": false } } } ] }
      ],
      "failureHandlers": [
        { "step": { "name": "triage", "kind": "custom", "image": "alpine", "output": null, "secrets": [],
                    "input": { "failure": { "ref": "failure" } }, "inputTypes": { "failure": { "model": "FailureContext", "optional": false } } },
          "steps": ["deploy"] }
      ]
    }
    """;

    public static DevManifest Manifest()
    {
        DevManifest.Parse(ManifestJson).TryPickProblems(out _, out var manifest);
        return manifest!;
    }

    public static readonly DevToolchain Tools = new("/fake/bun", "/fake/sdk/src/dev/cli.ts", "/fake/sdk/src/build/cli.ts");

    public static CliArgs Args(IReadOnlySet<string> booleans, params string[] args)
    {
        var flags = new HashSet<string>(booleans, StringComparer.Ordinal) { "json", "verbose", "help" };
        CliArgs.Parse(args, flags, new Dictionary<string, string>()).TryPickProblems(out _, out var cli);
        cli!.OperandOffset = 2;
        return cli;
    }

    public static CommandContext Context(bool json = false) => new()
    {
        Json = json,
        Verbose = false,
        Api = new FakePipelinesApi(),
        Output = new ConsoleOutputWriter(json, TextWriter.Null),
        Log = new StderrLog(false, TextWriter.Null),
        Config = new CliConfig(),
    };
}

/// <summary>A throwaway pipeline dir with a pipeline.ts and a .olve workspace, deleted on dispose.</summary>
internal sealed class TempPipelineDir : IDisposable
{
    public TempPipelineDir()
    {
        Dir = Path.Combine(Path.GetTempPath(), "pl-dev-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, "pipeline.ts"), "// fixture\n");
        Workspace = new DevWorkspace(Path.Combine(Dir, ".olve"));
    }

    public string Dir { get; }
    public DevWorkspace Workspace { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// Simulates Bun + the SDK dev runner: answers <c>describe</c> with a manifest and, for
/// <c>run &lt;dir&gt; &lt;step&gt; --olve-dir &lt;d&gt;</c>, reads input.json and writes work/output.json
/// (and work/files) like the harness, from a per-step script.
/// </summary>
internal sealed class FakeHarness(string manifest) : IProcessRunner
{
    public delegate (JsonObject Envelope, int ExitCode) StepScript(JsonObject input, string olveDir);

    public Dictionary<string, StepScript> Steps { get; } = new(StringComparer.Ordinal);
    public List<ProcessSpec> Calls { get; } = [];
    public Dictionary<string, JsonObject> Inputs { get; } = new(StringComparer.Ordinal);
    public int DescribeExitCode { get; set; }
    public string DescribeStderr { get; set; } = "";

    public IEnumerable<string> RanSteps => Calls.Where(c => c.Arguments.Count > 3 && c.Arguments[1] == "run").Select(c => c.Arguments[3]);

    public Task<Result<ProcessResult>> RunAsync(
        string fileName, IReadOnlyList<string> arguments, string? standardInput = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<Result<ProcessResult>> RunAsync(ProcessSpec spec, CancellationToken ct = default)
    {
        Calls.Add(spec);
        var args = spec.Arguments;
        if (args.Count >= 2 && args[1] == "describe")
        {
            return Task.FromResult(Result.Success(DescribeExitCode == 0
                ? new ProcessResult(0, manifest, "")
                : new ProcessResult(DescribeExitCode, "", DescribeStderr)));
        }

        if (args.Count >= 6 && args[1] == "run")
        {
            var step = args[3];
            var olveDir = args[5];
            var input = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(olveDir, "input.json")))!;
            Inputs[step] = input;
            var (envelope, exitCode) = Steps.TryGetValue(step, out var script)
                ? script(input, olveDir)
                : (new JsonObject { ["status"] = "succeeded", ["output"] = new JsonObject { ["from"] = step } }, 0);
            Directory.CreateDirectory(Path.Combine(olveDir, "work"));
            File.WriteAllText(Path.Combine(olveDir, "work", "output.json"), envelope.ToJsonString());
            spec.OnStdoutLine?.Invoke($"log from {step}");
            spec.OnStdoutLine?.Invoke(DevSession.ResultBegin);
            spec.OnStdoutLine?.Invoke(envelope.ToJsonString());
            spec.OnStdoutLine?.Invoke(DevSession.ResultEnd);
            return Task.FromResult(Result.Success(new ProcessResult(exitCode, "", "")));
        }

        return Task.FromResult(Result.Success(new ProcessResult(0, "", "")));
    }
}

/// <summary>Scripted answers to confirmation prompts; records the questions.</summary>
internal sealed class FakePrompt(ConfirmAnswer answer) : IConfirmPrompt
{
    public List<string> Questions { get; } = [];

    public ConfirmAnswer Confirm(string question)
    {
        Questions.Add(question);
        return answer;
    }
}
