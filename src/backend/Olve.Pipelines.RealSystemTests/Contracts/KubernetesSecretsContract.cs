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

        var created = await s.Client.CreateSecretAsync(s.Namespace, name, new() { ["A"] = "second" });

        await Assert.That(created).IsFalse();
        await Assert.That((await s.Client.GetSecretAsync(s.Namespace, name))!["A"]).IsEqualTo("first");
    }

    [Test]
    public async Task Revision_OfAMissingSecret_IsNull_AndOfOneWithoutValues_IsEmpty()
    {
        Rule();
        await using var s = await SubjectAsync();
        var name = s.NewName();

        await Assert.That(await s.Client.GetSecretRevisionAsync(s.Namespace, name)).IsNull();

        await s.Client.CreateSecretAsync(s.Namespace, name, []);

        await Assert.That((await s.Client.GetSecretRevisionAsync(s.Namespace, name))!.Data).IsEmpty();
    }

    [Test]
    public async Task ConditionalUpdate_AppliesOnlyAtTheVersionRead()
    {
        Rule();
        await using var s = await SubjectAsync();
        var name = s.NewName();
        await s.Client.CreateSecretAsync(s.Namespace, name, new() { ["A"] = "1" });
        var read = (await s.Client.GetSecretRevisionAsync(s.Namespace, name))!;

        var first = await s.Client.TryUpdateSecretAsync(s.Namespace, name, new() { ["A"] = "2" }, read.ResourceVersion);
        var stale = await s.Client.TryUpdateSecretAsync(s.Namespace, name, new() { ["A"] = "3" }, read.ResourceVersion);

        await Assert.That(first).IsTrue();
        await Assert.That(stale).IsFalse();
        await Assert.That((await s.Client.GetSecretAsync(s.Namespace, name))!["A"]).IsEqualTo("2");
    }

    [Test]
    public async Task ConditionalUpdate_OfADeletedSecret_IsFalse_AndDoesNotRecreateIt()
    {
        Rule();
        await using var s = await SubjectAsync();
        var name = s.NewName();
        await s.Client.CreateSecretAsync(s.Namespace, name, new() { ["A"] = "1" });
        var read = (await s.Client.GetSecretRevisionAsync(s.Namespace, name))!;
        await s.Client.DeleteSecretAsync(s.Namespace, name);

        var updated = await s.Client.TryUpdateSecretAsync(s.Namespace, name, new() { ["A"] = "2" }, read.ResourceVersion);

        await Assert.That(updated).IsFalse();
        await Assert.That(await s.Client.GetSecretRevisionAsync(s.Namespace, name)).IsNull();
    }

    [Test]
    public async Task ConditionalDelete_KeepsASecretThatChanged_AndAMissingOneIsFine()
    {
        Rule();
        await using var s = await SubjectAsync();
        var name = s.NewName();
        await s.Client.CreateSecretAsync(s.Namespace, name, new() { ["A"] = "1" });
        var read = (await s.Client.GetSecretRevisionAsync(s.Namespace, name))!;
        await s.Client.UpdateSecretAsync(s.Namespace, name, new() { ["A"] = "2" });

        await Assert.That(await s.Client.TryDeleteSecretAsync(s.Namespace, name, read.ResourceVersion)).IsFalse();
        await Assert.That((await s.Client.GetSecretAsync(s.Namespace, name))!["A"]).IsEqualTo("2");

        var current = (await s.Client.GetSecretRevisionAsync(s.Namespace, name))!;
        await Assert.That(await s.Client.TryDeleteSecretAsync(s.Namespace, name, current.ResourceVersion)).IsTrue();
        await Assert.That(await s.Client.TryDeleteSecretAsync(s.Namespace, name, current.ResourceVersion)).IsTrue();
        await Assert.That(await s.Client.GetSecretRevisionAsync(s.Namespace, name)).IsNull();
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
