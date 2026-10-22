using Olve.Pipelines.Cli;
using Olve.Pipelines.Cli.Api;

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
