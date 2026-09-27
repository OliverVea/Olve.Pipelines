using System.Text;
using Olve.Results;
using Olve.Pipelines.Cli;
using Olve.Pipelines.Cli.Commands;

namespace Olve.Pipelines.UnitTests.Cli;

public class BootstrapCommandTests
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

    private static ProcessResult NotFound() => new(1, "", "NotFound");

    // Answers all calls success; the storage-secret existence check is toggled by secretExists,
    // whether that secret already carries Garage's rpc-secret by hasRpcSecret, and which
    // storage deployments exist by minioExists/garageExists.
    private static FakeProcessRunner HappyRunner(
        bool secretExists, bool hasRpcSecret = true, bool minioExists = false, bool garageExists = false) => new((file, args) =>
    {
        if (file == "kubectl" && args.Contains("get") && args.Contains("deployment"))
        {
            if (args.Contains("olve-pipelines-minio")) return minioExists ? Ok() : NotFound();
            if (args.Contains("olve-pipelines-garage")) return garageExists ? Ok() : NotFound();
        }

        if (file == "kubectl" && args.Contains("get") && args.Contains("secret"))
        {
            if (args.Any(a => a.StartsWith("jsonpath", StringComparison.Ordinal)))
            {
                var value = args.Any(a => a.Contains("rpc-secret")) ? (hasRpcSecret ? "abc123" : "")
                    : args.Any(a => a.Contains("root-user")) ? "olve-pipelines" : "secret-pw";
                return Ok(Convert.ToBase64String(Encoding.UTF8.GetBytes(value)));
            }

            return secretExists ? Ok() : NotFound();
        }

        return Ok();
    });

    private static async Task<(Result Result, FakeProcessRunner Runner)> Bootstrap(FakeProcessRunner runner)
    {
        var chart = CreateTempChart();
        try
        {
            var result = await new BootstrapCommand(runner).RunAsync(Args("bootstrap", "-n", "pl-test", "--chart", chart));
            return (result, runner);
        }
        finally
        {
            Directory.Delete(chart, recursive: true);
        }
    }

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
        var result = await new BootstrapCommand(runner).RunAsync(Args("bootstrap"));

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(runner.Calls).IsEmpty();
    }

    [Test]
    public async Task ProdNamespace_WithoutAllowProd_Fails_WithoutRunningAnything()
    {
        var runner = new FakeProcessRunner((_, _) => Ok());
        var result = await new BootstrapCommand(runner).RunAsync(Args("bootstrap", "-n", BootstrapCommand.ProdNamespace));

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
            var result = await new BootstrapCommand(runner)
                .RunAsync(Args("bootstrap", "-n", "pl-test", "--chart", chart));

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
            var result = await new BootstrapCommand(runner)
                .RunAsync(Args("bootstrap", "-n", "pl-test", "--chart", chart));

            await Assert.That(result.Succeeded).IsTrue();
            // Idempotency-critical: never recreate the creds Secret on a re-run.
            await Assert.That(runner.Invoked("kubectl", "create", "secret")).IsFalse();
        }
        finally
        {
            Directory.Delete(chart, recursive: true);
        }
    }

    [Test]
    public async Task SecretAbsent_GeneratesRpcSecret_AndPointsControllerAtGarage()
    {
        var (result, runner) = await Bootstrap(HappyRunner(secretExists: false));

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(runner.Calls.Any(c => c.File == "kubectl" && c.Args.Contains("create")
            && c.Args.Any(a => a.StartsWith("--from-literal=rpc-secret=", StringComparison.Ordinal)))).IsTrue();
        await Assert.That(runner.Invoked("helm", "config.Storage__Endpoint=http://olve-pipelines-garage.pl-test:3900")).IsTrue();
        await Assert.That(runner.Invoked("kubectl", "rollout", "status", "deploy/olve-pipelines-garage")).IsTrue();
    }

    [Test]
    public async Task SecretPresent_WithoutRpcSecret_PatchesOnlyThatKey()
    {
        var (result, runner) = await Bootstrap(HappyRunner(secretExists: true, hasRpcSecret: false));

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(runner.Invoked("kubectl", "patch", "secret")).IsTrue();
        await Assert.That(runner.Invoked("kubectl", "create", "secret")).IsFalse();
    }

    [Test]
    public async Task SecretPresent_WithRpcSecret_DoesNotPatch()
    {
        var (result, runner) = await Bootstrap(HappyRunner(secretExists: true));

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(runner.Invoked("kubectl", "patch", "secret")).IsFalse();
    }

    [Test]
    public async Task LegacyMinioInstall_WithoutGarage_Refuses_BeforeTouchingAnything()
    {
        var (result, runner) = await Bootstrap(HappyRunner(secretExists: true, minioExists: true));

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(runner.Invoked("helm", "upgrade")).IsFalse();
        await Assert.That(runner.Invoked("kubectl", "create", "secret")).IsFalse();
        await Assert.That(runner.Invoked("kubectl", "patch", "secret")).IsFalse();
    }

    [Test]
    public async Task MinioAlongsideGarage_Proceeds()
    {
        var (result, _) = await Bootstrap(HappyRunner(secretExists: true, minioExists: true, garageExists: true));

        await Assert.That(result.Succeeded).IsTrue();
    }
}
