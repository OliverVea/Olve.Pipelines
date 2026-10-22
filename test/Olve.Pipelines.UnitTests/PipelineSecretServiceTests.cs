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

    private static Fixture Create(bool kubernetes = true)
    {
        var s = new SchedulerScenario(SchedulerScenario.Mutated(root =>
        {
            ManifestFixture.Step(root, "build")["secrets"] = new System.Text.Json.Nodes.JsonArray("REGISTRY");
            root["secrets"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject { ["name"] = "REGISTRY", ["description"] = "registry creds" });
        }));
        var bindings = new PipelineConfigBindingService(new EntityStore<PipelineConfigBinding>([]), new IdProvider());
        TestRunDomain.Pick(bindings.Create(s.Pipeline.Id, "o/r", "main", ".pipelines", "GITHUB_TOKEN"));

        var k8s = new PipelineSecretCopyServiceTests.FakeSecrets();
        var services = new ServiceCollection();
        services.AddSingleton(new KubernetesOptions("runners"));
        if (kubernetes)
            services.AddSingleton<IKubernetesClient>(k8s);
        else
            services.AddSingleton<IKubernetesClient>(_ => throw new InvalidOperationException("Kubernetes is not configured."));

        var service = new PipelineSecretService(s.D.PipelineService, bindings, s.D.ActivationService, s.D.Plans,
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
    public async Task WithoutKubernetes_ListReportsUnknown_AndChangesFailCleanly()
    {
        var f = Create(kubernetes: false);

        var secrets = TestRunDomain.Pick(await f.Service.ListAsync(f.S.Pipeline.Id));

        await Assert.That(secrets.Select(x => x.Name)).Contains("REGISTRY");
        await Assert.That(secrets.Select(x => x.Name)).Contains("GITHUB_TOKEN");
        await Assert.That(secrets.All(x => x.IsSet is null)).IsTrue();
        await Assert.That(await f.Service.SetAsync(f.S.Pipeline.Id, "REGISTRY", "v")).Failed();
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
}
