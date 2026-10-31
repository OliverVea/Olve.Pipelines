using Olve.Pipelines.Kubernetes;

namespace Olve.Pipelines.RealSystemTests.Contracts;

/// <summary>Pipeline secrets as the service relies on them.</summary>
public abstract class KubernetesSecretsContract : ContractSuite
{
    protected abstract Task<ContractSubject<IKubernetesClient>> SubjectAsync();

    [Test]
    public async Task Get_OfAMissingSecret_IsNull()
    {
        Rule();
        await using var s = await SubjectAsync();

        await Assert.That(await s.Client.GetSecretAsync(s.Namespace, s.NewName())).IsNull();
    }

    [Test]
    public async Task Create_ThenGet_RoundTripsTheValues()
    {
        Rule();
        await using var s = await SubjectAsync();
        var name = s.NewName();
        var data = new Dictionary<string, string> { ["TOKEN"] = "a=b+c/d", ["PEM"] = "line one\nline two\n", ["UNICODE"] = "æøå 日本語" };

        await s.Client.CreateSecretAsync(s.Namespace, name, data);

        await Assert.That(await s.Client.GetSecretAsync(s.Namespace, name)).IsEquivalentTo(data);
    }

    [Test]
    public async Task Create_OfAnExistingSecret_KeepsItsValues()
    {
        Rule();
        await using var s = await SubjectAsync();
        var name = s.NewName();
        await s.Client.CreateSecretAsync(s.Namespace, name, new() { ["A"] = "first" });

        await s.Client.CreateSecretAsync(s.Namespace, name, new() { ["A"] = "second" });

        await Assert.That((await s.Client.GetSecretAsync(s.Namespace, name))!["A"]).IsEqualTo("first");
    }

    [Test]
    public async Task Update_ReplacesAllValues()
    {
        Rule();
        await using var s = await SubjectAsync();
        var name = s.NewName();
        await s.Client.CreateSecretAsync(s.Namespace, name, new() { ["OLD"] = "1", ["KEPT"] = "2" });

        await s.Client.UpdateSecretAsync(s.Namespace, name, new() { ["KEPT"] = "3" });

        await Assert.That(await s.Client.GetSecretAsync(s.Namespace, name)).IsEquivalentTo(new Dictionary<string, string> { ["KEPT"] = "3" });
    }

    [Test]
    public async Task Update_OfAMissingSecret_Throws()
    {
        Rule();
        await using var s = await SubjectAsync();
        var name = s.NewName();

        var thrown = await Assert.That(() => s.Client.UpdateSecretAsync(s.Namespace, name, new() { ["A"] = "1" }))
            .Throws<HttpRequestException>();

        await Assert.That(thrown!.Message).StartsWith($"Failed to update K8s Secret '{name}': NotFound");
    }

    [Test]
    public async Task Delete_RemovesTheSecret_AndAMissingOneIsFine()
    {
        Rule();
        await using var s = await SubjectAsync();
        var name = s.NewName();
        await s.Client.CreateSecretAsync(s.Namespace, name, new() { ["A"] = "1" });

        await s.Client.DeleteSecretAsync(s.Namespace, name);
        await s.Client.DeleteSecretAsync(s.Namespace, name);

        await Assert.That(await s.Client.GetSecretAsync(s.Namespace, name)).IsNull();
    }

    [Test]
    public async Task Get_OfASecretWithNoValues_IsNull()
    {
        Rule();
        await using var s = await SubjectAsync();
        var name = s.NewName();

        await s.Client.CreateSecretAsync(s.Namespace, name, []);

        await Assert.That(await s.Client.GetSecretAsync(s.Namespace, name)).IsNull();
    }
}
