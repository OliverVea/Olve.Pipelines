using Olve.Pipelines.RealSystemTests.Hosting;

namespace Olve.Pipelines.UnitTests.RealSystem;

public class TargetGuardTests
{
    private static readonly Uri Api = new("http://localhost:5000");
    private static readonly Uri Throwaway = new("https://127.0.0.1:15643");

    [Test]
    [Arguments("https://pipelines-private.ovea.pro", "https://127.0.0.1:15643", "id", "Refusing https://pipelines-private.ovea.pro/: it is a real instance, not a test host.")]
    [Arguments("https://pipelines-beta.ovea.pro", "https://127.0.0.1:15643", "id", "Refusing https://pipelines-beta.ovea.pro/: it is a real instance, not a test host.")]
    [Arguments("http://localhost:5000", "https://127.0.0.1:6443", "id", "Refusing http://localhost:5000/: port 6443 of 127.0.0.1 is where a real cluster listens; test clusters use another port.")]
    [Arguments("http://localhost:5000", "https://127.0.0.1:15643", " ", "Refusing http://localhost:5000/: its descriptor names no test cluster id.")]
    public async Task Descriptor_IsRefused(string api, string kubernetes, string clusterId, string message)
    {
        await Assert.That(TargetGuard.RefuseDescriptor(new Uri(api), new Uri(kubernetes), clusterId)).IsEqualTo(message);
    }

    [Test]
    public async Task ThrowawayDescriptor_MayBeProbed()
    {
        await Assert.That(TargetGuard.RefuseDescriptor(Api, Throwaway, "id")).IsNull();
    }

    [Test]
    [Arguments(null, "Refusing the cluster at https://127.0.0.1:15643/: it has no olve-real-system-marker Secret, so it is not a test cluster.")]
    [Arguments("other", "Refusing the cluster at https://127.0.0.1:15643/: it is test cluster 'other', not 'id' as the descriptor says.")]
    public async Task Cluster_IsRefused(string? marker, string message)
    {
        await Assert.That(TargetGuard.RefuseCluster(Throwaway, "id", marker)).IsEqualTo(message);
    }

    [Test]
    public async Task MarkedCluster_IsAccepted()
    {
        await Assert.That(TargetGuard.RefuseCluster(Throwaway, "id", "id")).IsNull();
    }
}

public class TestTargetsTests
{
    [Test]
    public async Task NoConfiguration_IsTheLocalHost()
    {
        await Assert.That(TestTargets.ListUrls(_ => null, _ => "")).IsEquivalentTo(["http://localhost:5000"]);
    }

    [Test]
    public async Task TestTargets_ListsHosts_OverTheFile()
    {
        var urls = TestTargets.ListUrls(
            name => name == "TEST_TARGETS" ? "http://a:5000, http://b:5010" : "file",
            _ => "http://ignored:1");

        await Assert.That(urls).IsEquivalentTo(["http://a:5000", "http://b:5010"]);
    }

    [Test]
    public async Task TheFile_HasOneHostPerLine()
    {
        var urls = TestTargets.ListUrls(
            name => name == "TEST_TARGETS_FILE" ? "hosts.txt" : null,
            file => file == "hosts.txt" ? "http://a:5000\nhttp://b:5010\n" : "");

        await Assert.That(urls).IsEquivalentTo(["http://a:5000", "http://b:5010"]);
    }

    [Test]
    public async Task EachHost_HasADescriptorNamedAfterIt()
    {
        await Assert.That(TestTargets.DescriptorName("http://localhost:5010")).IsEqualTo("localhost-5010.json");
    }
}
