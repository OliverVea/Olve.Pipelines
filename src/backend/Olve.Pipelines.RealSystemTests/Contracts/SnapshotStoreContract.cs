using System.Text;
using Olve.Pipelines.Shared.Persistence;

namespace Olve.Pipelines.RealSystemTests.Contracts;

/// <summary>The persistence store: state must come back byte for byte, and a missing key is not an error.</summary>
public abstract class SnapshotStoreContract : ContractSuite
{
    protected abstract Task<ContractSubject<ISnapshotStore>> SubjectAsync();

    [Test]
    public async Task Read_OfAMissingKey_IsNull()
    {
        Rule();
        await using var s = await SubjectAsync();

        await Assert.That(await s.Client.TryReadAsync(s.NewName(), CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task Write_ThenRead_RoundTripsTheBytes_AndOverwrites()
    {
        Rule();
        await using var s = await SubjectAsync();
        var key = $"{s.NewName()}/runs.json";
        byte[] binary = [0, 255, 10, 13, .. Encoding.UTF8.GetBytes("æøå")];

        await s.Client.WriteAsync(key, Encoding.UTF8.GetBytes("first"), CancellationToken.None);
        await s.Client.WriteAsync(key, binary, CancellationToken.None);

        await Assert.That(await s.Client.TryReadAsync(key, CancellationToken.None)).IsEquivalentTo(binary);
    }

    [Test]
    public async Task Write_OfNoBytes_ReadsBackEmpty()
    {
        Rule();
        await using var s = await SubjectAsync();
        var key = s.NewName();

        await s.Client.WriteAsync(key, [], CancellationToken.None);

        await Assert.That(await s.Client.TryReadAsync(key, CancellationToken.None)).IsEmpty();
    }

    [Test]
    public async Task Delete_RemovesTheKey_AndAMissingOneIsFine()
    {
        Rule();
        await using var s = await SubjectAsync();
        var key = s.NewName();
        await s.Client.WriteAsync(key, [1], CancellationToken.None);

        await s.Client.DeleteAsync(key, CancellationToken.None);
        await s.Client.DeleteAsync(key, CancellationToken.None);

        await Assert.That(await s.Client.TryReadAsync(key, CancellationToken.None)).IsNull();
    }
}
