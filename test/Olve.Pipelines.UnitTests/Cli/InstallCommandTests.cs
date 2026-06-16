using System.Text;
using Olve.Results;
using Olve.Pipelines.Cli;
using Olve.Pipelines.Cli.Commands;

namespace Olve.Pipelines.UnitTests.Cli;

public class InstallCommandTests
{
    private static readonly HashSet<string> Booleans = new(StringComparer.Ordinal) { "allow-prod", "purge-data", "help" };
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal) { ["n"] = "namespace" };

    private static CliArgs Args(params string[] args)
    {
        CliArgs.Parse(args, Booleans, Aliases).TryPickProblems(out _, out var cli);
        return cli!;
    }

    /// <summary>Records every invocation and answers from a scripted responder.</summary>
    private sealed class FakeProcessRunner(Func<string, IReadOnlyList<string>, ProcessResult> responder) : IProcessRunner
    {
        public List<(string File, IReadOnlyList<string> Args)> Calls { get; } = [];

        public Task<Result<ProcessResult>> RunAsync(
            string fileName, IReadOnlyList<string> arguments, string? standardInput = null, CancellationToken ct = default)
        {
            Calls.Add((fileName, arguments));
            return Task.FromResult(Result.Success(responder(fileName, arguments)));
        }

        public bool Invoked(string file, params string[] mustContain) =>
            Calls.Any(c => c.File == file && mustContain.All(c.Args.Contains));
    }

    private static ProcessResult Ok(string stdout = "") => new(0, stdout, "");

    // Answers all calls success; the MinIO-secret existence check is toggled by secretExists.
    private static FakeProcessRunner HappyRunner(bool secretExists) => new((file, args) =>
    {
        if (file == "kubectl" && args.Contains("get") && args.Contains("secret"))
        {
            if (args.Any(a => a.StartsWith("jsonpath", StringComparison.Ordinal)))
            {
                var value = args.Any(a => a.Contains("root-user")) ? "olve-pipelines" : "secret-pw";
                return Ok(Convert.ToBase64String(Encoding.UTF8.GetBytes(value)));
            }

            return secretExists ? Ok() : new ProcessResult(1, "", "NotFound");
        }

        return Ok();
    });

    private static string CreateTempChart()
    {
        var dir = Directory.CreateTempSubdirectory("pl-test-chart-").FullName;
        File.WriteAllText(Path.Combine(dir, "Chart.yaml"), "name: test\nversion: 0\n");
        File.WriteAllText(Path.Combine(dir, "values-minimal.yaml"), "");
        return dir;
    }

    [Test]
    public async Task MissingNamespace_Fails_WithoutRunningAnything()
    {
        var runner = new FakeProcessRunner((_, _) => Ok());
        var result = await new InstallCommand(runner).RunAsync(Args("install"));

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(runner.Calls).IsEmpty();
    }

    [Test]
    public async Task ProdNamespace_WithoutAllowProd_Fails_WithoutRunningAnything()
    {
        var runner = new FakeProcessRunner((_, _) => Ok());
        var result = await new InstallCommand(runner).RunAsync(Args("install", "-n", InstallCommand.ProdNamespace));

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(runner.Calls).IsEmpty();
    }

    [Test]
    public async Task SecretAbsent_GeneratesSecret()
    {
        var chart = CreateTempChart();
        try
        {
            var runner = HappyRunner(secretExists: false);
            var result = await new InstallCommand(runner)
                .RunAsync(Args("install", "-n", "pl-test", "--chart", chart));

            await Assert.That(result.Succeeded).IsTrue();
            await Assert.That(runner.Invoked("kubectl", "create", "secret")).IsTrue();
        }
        finally
        {
            Directory.Delete(chart, recursive: true);
        }
    }

    [Test]
    public async Task SecretPresent_LeavesItUntouched()
    {
        var chart = CreateTempChart();
        try
        {
            var runner = HappyRunner(secretExists: true);
            var result = await new InstallCommand(runner)
                .RunAsync(Args("install", "-n", "pl-test", "--chart", chart));

            await Assert.That(result.Succeeded).IsTrue();
            // Idempotency-critical: never recreate the creds Secret on a re-run.
            await Assert.That(runner.Invoked("kubectl", "create", "secret")).IsFalse();
        }
        finally
        {
            Directory.Delete(chart, recursive: true);
        }
    }
}
