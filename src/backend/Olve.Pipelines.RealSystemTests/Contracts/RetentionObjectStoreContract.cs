using Olve.Pipelines.Pipelines.Retention;

namespace Olve.Pipelines.RealSystemTests.Contracts;

/// <summary>Bucket listing and bulk delete as retention relies on them.</summary>
public abstract class RetentionObjectStoreContract : ContractSuite
{
    public sealed record Store(IRetentionObjectStore Objects, Func<string, string, Task> PutAsync);

    protected abstract Task<ContractSubject<Store>> SubjectAsync();

    [Test]
    public async Task List_ReturnsKeysUnderThePrefix_WithSizeAndAge()
    {
        Rule();
        await using var s = await SubjectAsync();
        var root = s.NewName();
        var started = DateTimeOffset.UtcNow.AddMinutes(-1);
        await s.Client.PutAsync($"{root}/a/1.txt", "12345");
        await s.Client.PutAsync($"{root}/a/2.txt", "");
        await s.Client.PutAsync($"{root}/ab/3.txt", "x");

        var listed = await s.Client.Objects.ListAsync($"{root}/a/", CancellationToken.None);

        await Assert.That(listed.Select(o => (o.Key, o.Size))).IsEquivalentTo([($"{root}/a/1.txt", 5L), ($"{root}/a/2.txt", 0L)]);
        await Assert.That(listed.All(o => o.LastModified > started && o.LastModified < started.AddMinutes(3))).IsTrue();
    }

    [Test]
    public async Task List_OfAnEmptyPrefix_IsEmpty()
    {
        Rule();
        await using var s = await SubjectAsync();

        await Assert.That(await s.Client.Objects.ListAsync($"{s.NewName()}/", CancellationToken.None)).IsEmpty();
    }

    [Test]
    public async Task List_PagesPastOneThousandKeys()
    {
        Rule();
        await using var s = await SubjectAsync();
        var root = s.NewName();
        const int count = 1005;
        await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (i, _) => await s.Client.PutAsync($"{root}/{i:D5}", "x"));

        var listed = await s.Client.Objects.ListAsync($"{root}/", CancellationToken.None);

        await Assert.That(listed.Count).IsEqualTo(count);
    }

    [Test]
    public async Task Delete_RemovesTheKeys_AndMissingOnesDoNotFail()
    {
        Rule();
        await using var s = await SubjectAsync();
        var root = s.NewName();
        await s.Client.PutAsync($"{root}/gone", "x");
        await s.Client.PutAsync($"{root}/kept", "x");

        var failed = await s.Client.Objects.DeleteAsync([$"{root}/gone", $"{root}/never-existed"], CancellationToken.None);

        await Assert.That(failed).IsEqualTo(0);
        var left = await s.Client.Objects.ListAsync($"{root}/", CancellationToken.None);
        await Assert.That(left.Select(o => o.Key)).IsEquivalentTo([$"{root}/kept"]);
    }
}
