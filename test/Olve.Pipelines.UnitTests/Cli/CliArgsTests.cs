using Olve.Results;
using Olve.Pipelines.Cli;

namespace Olve.Pipelines.UnitTests.Cli;

public class CliArgsTests
{
    private static readonly HashSet<string> Booleans = new(StringComparer.Ordinal) { "allow-prod", "help" };
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal) { ["n"] = "namespace" };

    private static CliArgs Parse(params string[] args)
    {
        var result = CliArgs.Parse(args, Booleans, Aliases);
        if (result.TryPickProblems(out _, out var cli))
            throw new InvalidOperationException("expected parse success");
        return cli;
    }

    [Test]
    public async Task Parse_CommandAndOption()
    {
        var cli = Parse("install", "--namespace", "pl-test");
        await Assert.That(cli.Command).IsEqualTo("install");
        await Assert.That(cli.Option("namespace")).IsEqualTo("pl-test");
    }

    [Test]
    public async Task Parse_ShortAlias_MapsToLong()
    {
        var cli = Parse("install", "-n", "pl-test");
        await Assert.That(cli.Option("namespace")).IsEqualTo("pl-test");
    }

    [Test]
    public async Task Parse_InlineValue()
    {
        var cli = Parse("install", "--release=custom");
        await Assert.That(cli.Option("release")).IsEqualTo("custom");
    }

    [Test]
    public async Task Parse_BooleanFlag()
    {
        var cli = Parse("install", "--allow-prod");
        await Assert.That(cli.HasFlag("allow-prod")).IsTrue();
        await Assert.That(cli.HasFlag("help")).IsFalse();
    }

    [Test]
    public async Task Option_FallbackWhenAbsent()
    {
        var cli = Parse("install");
        await Assert.That(cli.Option("release", "olve-pipelines")).IsEqualTo("olve-pipelines");
    }

    [Test]
    public async Task Parse_MissingOptionValue_Fails()
    {
        var result = CliArgs.Parse(["install", "--namespace"], Booleans, Aliases);
        await Assert.That(result.Failed).IsTrue();
    }

    [Test]
    public async Task Parse_BooleanFlagWithValue_Fails()
    {
        var result = CliArgs.Parse(["install", "--allow-prod=yes"], Booleans, Aliases);
        await Assert.That(result.Failed).IsTrue();
    }

    [Test]
    public async Task Parse_UnexpectedSecondPositional_Fails()
    {
        var result = CliArgs.Parse(["install", "extra"], Booleans, Aliases);
        await Assert.That(result.Failed).IsTrue();
    }
}
