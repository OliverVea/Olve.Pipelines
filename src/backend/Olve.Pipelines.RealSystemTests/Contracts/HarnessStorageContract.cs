using Olve.Pipelines.Kubernetes.Harness;

namespace Olve.Pipelines.RealSystemTests.Contracts;

/// <summary>The object calls the executor makes around an attempt.</summary>
public abstract class HarnessStorageContract : ContractSuite
{
    protected abstract Task<ContractSubject<IHarnessStorage>> SubjectAsync();

    [Test]
    public async Task GetText_OfAMissingKey_IsNull()
    {
        Rule();
        await using var s = await SubjectAsync();

        await Assert.That(await s.Client.GetTextAsync(s.NewName())).IsNull();
    }

    [Test]
    public async Task PutText_ThenGetText_RoundTrips()
    {
        Rule();
        await using var s = await SubjectAsync();
        var key = s.NewName();

        await s.Client.PutTextAsync(key, """{"name":"æøå"}""", "application/json");

        await Assert.That(await s.Client.GetTextAsync(key)).IsEqualTo("""{"name":"æøå"}""");
    }

    [Test]
    public async Task Exists_FollowsPutAndDelete_AndDeletingAMissingKeyIsFine()
    {
        Rule();
        await using var s = await SubjectAsync();
        var key = s.NewName();

        await Assert.That(await s.Client.ExistsAsync(key)).IsFalse();
        await s.Client.PutTextAsync(key, "x", "text/plain");
        await Assert.That(await s.Client.ExistsAsync(key)).IsTrue();
        await s.Client.DeleteAsync(key);
        await s.Client.DeleteAsync(key);
        await Assert.That(await s.Client.ExistsAsync(key)).IsFalse();
    }

    [Test]
    public async Task Copy_DuplicatesTheObject()
    {
        Rule();
        await using var s = await SubjectAsync();
        var source = $"{s.NewName()}/source";
        await s.Client.PutTextAsync(source, "payload", "text/plain");

        await s.Client.CopyAsync(source, $"{source}-copy");

        await Assert.That(await s.Client.GetTextAsync($"{source}-copy")).IsEqualTo("payload");
        await Assert.That(await s.Client.GetTextAsync(source)).IsEqualTo("payload");
    }
}
