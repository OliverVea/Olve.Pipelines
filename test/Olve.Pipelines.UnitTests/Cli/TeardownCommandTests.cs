using Olve.Results;
using Olve.Pipelines.Cli;
using Olve.Pipelines.Cli.Commands;

namespace Olve.Pipelines.UnitTests.Cli;

public class TeardownCommandTests
{
    private static readonly HashSet<string> Booleans =
        new(StringComparer.Ordinal) { "allow-prod", "purge-data", "help" };
    private static readonly Dictionary<string, string> Aliases =
        new(StringComparer.Ordinal) { ["n"] = "namespace" };

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

    // Answers all calls success; `helm status` reports whether the release exists.
    private static FakeProcessRunner HappyRunner(bool releaseExists) => new((file, args) =>
    {
        if (file == "helm" && args.Contains("status"))
            return releaseExists ? Ok() : new ProcessResult(1, "", "Error: release: not found");

        return Ok();
    });

    [Test]
    public async Task MissingNamespace_Fails_WithoutRunningAnything()
    {
        var runner = new FakeProcessRunner((_, _) => Ok());
        var result = await new TeardownCommand(runner).RunAsync(Args("teardown"));

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(runner.Calls).IsEmpty();
    }

    [Test]
    public async Task ProdNamespace_WithoutAllowProd_Fails_WithoutRunningAnything()
    {
        var runner = new FakeProcessRunner((_, _) => Ok());
        var result = await new TeardownCommand(runner)
            .RunAsync(Args("teardown", "-n", BootstrapCommand.ProdNamespace));

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(runner.Calls).IsEmpty();
    }

    [Test]
    public async Task Default_UninstallsRelease_DeletesSecret_RetainsPvc()
    {
        var runner = HappyRunner(releaseExists: true);
        var result = await new TeardownCommand(runner).RunAsync(Args("teardown", "-n", "pl-test"));

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(runner.Invoked("helm", "uninstall")).IsTrue();
        await Assert.That(runner.Invoked("kubectl", "delete", "secret")).IsTrue();
        // Retain-by-default: the data PVC must NOT be deleted without --purge-data.
        await Assert.That(runner.Invoked("kubectl", "delete", "pvc")).IsFalse();
    }

    [Test]
    public async Task PurgeData_DeletesPvc()
    {
        var runner = HappyRunner(releaseExists: true);
        var result = await new TeardownCommand(runner)
            .RunAsync(Args("teardown", "-n", "pl-test", "--purge-data"));

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(runner.Invoked("kubectl", "delete", "pvc")).IsTrue();
    }

    [Test]
    public async Task MissingRelease_StillSucceeds_AndSkipsHelmUninstall()
    {
        var runner = HappyRunner(releaseExists: false);
        var result = await new TeardownCommand(runner).RunAsync(Args("teardown", "-n", "pl-test"));

        // Idempotency: a non-existent release is "already gone", not an error.
        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(runner.Invoked("helm", "uninstall")).IsFalse();
        // The creds Secret cleanup still runs (it lives outside the helm release).
        await Assert.That(runner.Invoked("kubectl", "delete", "secret")).IsTrue();
    }
}
