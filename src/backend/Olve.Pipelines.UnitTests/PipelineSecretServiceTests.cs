using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.UnitTests.Manifests;
using Olve.Pipelines.UnitTests.Scheduling;
using Olve.Results.TUnit;

namespace Olve.Pipelines.UnitTests;

public class PipelineSecretServiceTests
{
    private sealed record Fixture(SchedulerScenario S, PipelineSecretCopyServiceTests.FakeSecrets K8s, PipelineSecretService Service);

    private static Fixture Create(bool kubernetes = true, string? manifest = null)
    {
        var s = new SchedulerScenario(manifest ?? SchedulerScenario.Mutated(root =>
        {
            ManifestFixture.Step(root, "build")["secrets"] = new System.Text.Json.Nodes.JsonArray("REGISTRY");
            root["secrets"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject { ["name"] = "REGISTRY", ["description"] = "registry creds" });
        }));
        var bindings = new PipelineConfigBindingService(new EntityStore<PipelineConfigBinding>([]), new IdProvider(), TimeProvider.System);
        TestRunDomain.Pick(bindings.Create(s.Pipeline.Id, "o/r", "main", ".pipelines", "GITHUB_TOKEN"));

        var k8s = new PipelineSecretCopyServiceTests.FakeSecrets();
        var services = new ServiceCollection();
        services.AddSingleton(new KubernetesOptions("runners"));
        if (kubernetes)
            services.AddSingleton<IKubernetesClient>(k8s);
        else
            services.AddSingleton<IKubernetesClient>(_ => throw new InvalidOperationException("Kubernetes is not configured."));

        var service = new PipelineSecretService(new PipelineSecretEvents(), s.D.PipelineService, bindings, s.D.ActivationService, s.D.Plans, s.D.ConfigSnapshots,
            services.BuildServiceProvider(), NullLogger<PipelineSecretService>.Instance);
        return new Fixture(s, k8s, service);
    }

    [Test]
    public async Task List_UnionsDeclaredAndSetSecrets_WithUsersAndState()
    {
        var f = Create();
        f.K8s.Secrets[PipelineSecrets.Name(f.S.Pipeline.Id)] = new() { ["REGISTRY"] = "x", ["LEFTOVER"] = "y" };

        var secrets = TestRunDomain.Pick(await f.Service.ListAsync(f.S.Pipeline.Id));

        await Assert.That(secrets.Select(x => x.Name)).Contains("LEFTOVER");
        var registry = secrets.Single(x => x.Name == "REGISTRY");
        await Assert.That(registry.DeclaredBy).IsEquivalentTo(["build"]);
        await Assert.That(registry.Description).IsEqualTo("registry creds");
        await Assert.That(registry.IsSet).IsTrue();
        await Assert.That(secrets.Single(x => x.Name == "GITHUB_TOKEN").DeclaredBy).Contains("binding");
        await Assert.That(secrets.Single(x => x.Name == "GITHUB_TOKEN").IsSet).IsFalse();
        await Assert.That(secrets.Single(x => x.Name == "LEFTOVER").DeclaredBy).IsEmpty();
    }

    [Test]
    public async Task ASecretTheConfigCanReference_IsNeededOnlyWhereItDoes()
    {
        var f = Create(manifest: Configs.ConfigManifests.Text());
        var secret = Configs.ConfigManifests.Secret;

        TestRunDomain.Pick(f.S.D.ConfigSnapshots.Add(f.S.Pipeline.Id, "auth: { mode: none }\ntarget: { host: h }\n", "dev"));
        var unused = TestRunDomain.Pick(await f.Service.ListAsync(f.S.Pipeline.Id)).Single(x => x.Name == secret);
        TestRunDomain.Pick(f.S.D.ConfigSnapshots.Add(f.S.Pipeline.Id,
            $"auth: {{ mode: dex, clientId: c, clientSecret: {{ secret: {secret} }} }}\ntarget: {{ host: h }}\n", "dev"));
        var used = TestRunDomain.Pick(await f.Service.ListAsync(f.S.Pipeline.Id)).Single(x => x.Name == secret);

        await Assert.That(unused.DeclaredBy).IsEquivalentTo(["hello"]);
        await Assert.That(unused.Needed).IsFalse();
        await Assert.That(used.Needed).IsTrue();
    }

    [Test]
    public async Task AStepSecret_IsNeeded_AndAnUndeclaredOneIsNot()
    {
        var f = Create();
        f.K8s.Secrets[PipelineSecrets.Name(f.S.Pipeline.Id)] = new() { ["LEFTOVER"] = "y" };

        var secrets = TestRunDomain.Pick(await f.Service.ListAsync(f.S.Pipeline.Id));

        await Assert.That(secrets.Single(x => x.Name == "REGISTRY").Needed).IsTrue();
        await Assert.That(secrets.Single(x => x.Name == "LEFTOVER").Needed).IsFalse();
    }

    [Test]
    public async Task WithoutKubernetes_ListReportsUnknown_AndChangesFailCleanly()
    {
        var f = Create(kubernetes: false);

        var secrets = TestRunDomain.Pick(await f.Service.ListAsync(f.S.Pipeline.Id));

        await Assert.That(secrets.Select(x => x.Name)).Contains("REGISTRY");
        await Assert.That(secrets.Select(x => x.Name)).Contains("GITHUB_TOKEN");
        await Assert.That(secrets.All(x => x.IsSet is null)).IsTrue();
        await (await f.Service.SetAsync(f.S.Pipeline.Id, "REGISTRY", "v")).Problems.IsSingle<UnavailableProblem>("Kubernetes is not configured or unreachable; pipeline secrets cannot be changed.");
    }

    private static void RaceOnce(Fixture f, Action write)
    {
        var raced = false;
        f.K8s.AfterRead = () =>
        {
            if (raced)
                return;
            raced = true;
            write();
        };
    }

    // Catches: a first set that lost the create race dropping its value, the bug of two parallel PUTs on a new pipeline.
    [Test]
    public async Task Set_ThatLosesTheRaceToCreateTheSecret_KeepsBothValues()
    {
        var f = Create();
        var name = PipelineSecrets.Name(f.S.Pipeline.Id);
        RaceOnce(f, () => f.K8s.CreateSecretAsync("runners", name, new() { ["OTHER"] = "theirs" }).GetAwaiter().GetResult());

        await Assert.That(await f.Service.SetAsync(f.S.Pipeline.Id, "MINE", "ours")).Succeeded();

        await Assert.That(f.K8s.Secrets[name]).IsEquivalentTo(new Dictionary<string, string> { ["OTHER"] = "theirs", ["MINE"] = "ours" });
    }

    [Test]
    public async Task Set_OverAConcurrentWriteOfAnotherKey_KeepsBothValues()
    {
        var f = Create();
        var name = PipelineSecrets.Name(f.S.Pipeline.Id);
        f.K8s.Secrets[name] = new() { ["FIRST"] = "1" };
        RaceOnce(f, () => f.K8s.UpdateSecretAsync("runners", name, new() { ["FIRST"] = "1", ["OTHER"] = "theirs" }).GetAwaiter().GetResult());

        await Assert.That(await f.Service.SetAsync(f.S.Pipeline.Id, "MINE", "ours")).Succeeded();

        await Assert.That(f.K8s.Secrets[name]).IsEquivalentTo(new Dictionary<string, string> { ["FIRST"] = "1", ["OTHER"] = "theirs", ["MINE"] = "ours" });
    }

    // Catches: removing the last key deleting a value another writer just added.
    [Test]
    public async Task Delete_OfTheLastKey_KeepsAValueSetMeanwhile()
    {
        var f = Create();
        var name = PipelineSecrets.Name(f.S.Pipeline.Id);
        f.K8s.Secrets[name] = new() { ["ONLY"] = "1" };
        RaceOnce(f, () => f.K8s.UpdateSecretAsync("runners", name, new() { ["ONLY"] = "1", ["OTHER"] = "theirs" }).GetAwaiter().GetResult());

        await Assert.That(await f.Service.DeleteAsync(f.S.Pipeline.Id, "ONLY")).Succeeded();

        await Assert.That(f.K8s.Secrets[name]).IsEquivalentTo(new Dictionary<string, string> { ["OTHER"] = "theirs" });
    }

    [Test]
    public async Task Delete_OfTheLastKey_RemovesTheSecret()
    {
        var f = Create();
        var name = PipelineSecrets.Name(f.S.Pipeline.Id);
        f.K8s.Secrets[name] = new() { ["ONLY"] = "1" };

        await Assert.That(await f.Service.DeleteAsync(f.S.Pipeline.Id, "ONLY")).Succeeded();

        await Assert.That(f.K8s.Secrets.ContainsKey(name)).IsFalse();
    }

    // Writers that never stop leave no version to write at: the caller is told, nothing is silently dropped.
    [Test]
    public async Task Set_UnderEndlessConcurrentWrites_FailsWithAConflict()
    {
        var f = Create();
        var name = PipelineSecrets.Name(f.S.Pipeline.Id);
        f.K8s.Secrets[name] = new() { ["FIRST"] = "1" };
        f.K8s.AfterRead = () => f.K8s.UpdateSecretAsync("runners", name, new() { ["FIRST"] = "1" }).GetAwaiter().GetResult();

        var result = await f.Service.SetAsync(f.S.Pipeline.Id, "MINE", "ours");

        await Assert.That(result.TryPickProblems(out var problems) ? problems.Single().FormattedMessage : "").IsEqualTo($"Secret '{name}' kept changing under concurrent writes; try again.");
    }

    [Test]
    public async Task DeleteAll_RemovesThePipelinesSecret()
    {
        var f = Create();
        var name = PipelineSecrets.Name(f.S.Pipeline.Id);
        f.K8s.Secrets[name] = new() { ["REGISTRY"] = "x" };

        await f.Service.DeleteAllAsync(f.S.Pipeline.Id);

        await Assert.That(f.K8s.Secrets.ContainsKey(name)).IsFalse();
    }

    // Catches: a deleted pipeline's secret values left in the cluster for good; retention only sweeps URL Secrets.
    [Test]
    public async Task DeletingAPipeline_DeletesItsSecret()
    {
        var f = Create();
        var name = PipelineSecrets.Name(f.S.Pipeline.Id);
        f.K8s.Secrets[name] = new() { ["REGISTRY"] = "x" };
        var services = new ServiceCollection().AddSingleton(f.Service).BuildServiceProvider();
        await Assert.That(new PipelineSecretEventRegistration(f.S.D.PipelineEvents, services).Run()).Succeeded();

        await Assert.That(f.S.D.PipelineService.Delete(f.S.Pipeline.Id)).Succeeded();

        await Assert.That(f.K8s.Secrets.ContainsKey(name)).IsFalse();
    }
}
