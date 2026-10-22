using Olve.Pipelines.Cli;
using Olve.Pipelines.Cli.Api;
using Olve.Results.TUnit;

namespace Olve.Pipelines.UnitTests.Cli;

[NotInParallel(nameof(CliConfigStoreTests))]
public class CliConfigStoreTests
{
    private static async Task WithConfigFile(string? contents, Func<string, Task> test)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pl-config-{Guid.NewGuid():N}");
        if (contents is not null)
            await File.WriteAllTextAsync(path, contents);
        Environment.SetEnvironmentVariable(CliConfigStore.PathEnvVar, path);
        try
        {
            await test(path);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CliConfigStore.PathEnvVar, null);
            Environment.SetEnvironmentVariable(CliConfigStore.ProfileEnvVar, null);
            File.Delete(path);
        }
    }

    [Test]
    public async Task PreProfileFile_ReadsAsDefault_AndIsRewrittenWithProfiles() => await WithConfigFile(
        """{ "apiUrl": "https://old", "auth": { "accessToken": "t" } }""",
        async path =>
        {
            var config = TestRunDomain.Pick(CliConfigStore.Load());
            await Assert.That(config.Profile).IsEqualTo("default");
            await Assert.That(config.ApiUrl).IsEqualTo("https://old");
            await Assert.That(config.Auth!.AccessToken).IsEqualTo("t");

            await Assert.That(CliConfigStore.Save(config).Succeeded).IsTrue();
            var text = await File.ReadAllTextAsync(path);
            await Assert.That(text).Contains("\"profiles\"");
            await Assert.That(TestRunDomain.Pick(CliConfigStore.Load()).ApiUrl).IsEqualTo("https://old");
        });

    [Test]
    public async Task Save_WritesAnOwnerOnlyFile_WithoutLeftovers() => await WithConfigFile(null, async path =>
    {
        await Assert.That(CliConfigStore.Save(new CliConfig { ApiUrl = "https://x" })).Succeeded();

        if (!OperatingSystem.IsWindows())
            await Assert.That(File.GetUnixFileMode(path)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await Assert.That(string.Join(",", Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*").Where(f => f != path))).IsEqualTo("");
    });

    [Test]
    public async Task ParallelSaves_OfDifferentProfiles_AllLand() => await WithConfigFile(null, async _ =>
    {
        var names = Enumerable.Range(0, 8).Select(i => $"p{i}").ToList();
        await Task.WhenAll(names.Select(n => Task.Run(() => CliConfigStore.Save(new CliConfig { Profile = n, ApiUrl = $"https://{n}" }))));

        foreach (var n in names)
            await Assert.That(TestRunDomain.Pick(CliConfigStore.Load(n)).ApiUrl).IsEqualTo($"https://{n}");
    });

    [Test]
    public async Task SavingOneProfile_KeepsTheOthers() => await WithConfigFile(null, async _ =>
    {
        await Assert.That(CliConfigStore.Save(new CliConfig { ApiUrl = "https://prod" }).Succeeded).IsTrue();
        await Assert.That(CliConfigStore.Save(new CliConfig { Profile = "beta", ApiUrl = "https://beta" }).Succeeded).IsTrue();

        var profiles = TestRunDomain.Pick(CliConfigStore.LoadProfiles());
        await Assert.That(profiles.Keys).IsEquivalentTo(["default", "beta"]);
        await Assert.That(TestRunDomain.Pick(CliConfigStore.Load("beta")).ApiUrl).IsEqualTo("https://beta");
        await Assert.That(TestRunDomain.Pick(CliConfigStore.Load()).ApiUrl).IsEqualTo("https://prod");
        await Assert.That(TestRunDomain.Pick(CliConfigStore.Load("missing")).ApiUrl).IsNull();
    });

    [Test]
    public async Task Profile_FlagBeatsEnv_EnvBeatsDefault() => await WithConfigFile(null, async _ =>
    {
        static CliArgs Args(params string[] args) =>
            TestRunDomain.Pick(CliArgs.Parse(args, new HashSet<string>(), new Dictionary<string, string>()));

        await Assert.That(CliConfigStore.ResolveProfile(Args("pipeline", "list"))).IsEqualTo("default");
        Environment.SetEnvironmentVariable(CliConfigStore.ProfileEnvVar, "beta");
        await Assert.That(CliConfigStore.ResolveProfile(Args("pipeline", "list"))).IsEqualTo("beta");
        await Assert.That(CliConfigStore.ResolveProfile(Args("pipeline", "list", "--profile", "prod"))).IsEqualTo("prod");
    });
}
