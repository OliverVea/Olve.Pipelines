using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Sync.ConfigSource;
using Olve.Results;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.RealSystemTests.Contracts;

/// <summary>A binding's repository as the deploy poll reads it: an idle poll is free, and an ETag the source can't use never hides a change.</summary>
public abstract class ConfigSourceContract : ContractSuite
{
    public sealed record Repository(IConfigSource Source, string BranchHead, string ConfigSha);

    protected abstract Task<ContractSubject<Repository>> SubjectAsync();

    private static PipelineConfigBinding Binding() => new(
        Id.New<PipelineConfigBinding>(), Id.New<Pipeline>(), "OliverVea/Olve.Pipelines", "main", ".pipelines", null, null, null,
        ReconcileStatus.NeverRun, DateTimeOffset.UnixEpoch);

    private static async Task<ConfigFetch.Changed> ChangedAsync(Repository repository, string? etag)
    {
        var result = await repository.Source.FetchConfigAsync(Binding(), etag);
        if (result.TryPickProblems(out var problems, out var fetch))
            throw new InvalidOperationException(string.Join("; ", problems.Select(p => p.Message)));

        return (await Assert.That(fetch).IsTypeOf<ConfigFetch.Changed>())!;
    }

    [Test]
    public async Task BranchHead_IsTheNewestCommitOfTheBranch()
    {
        Rule();
        await using var s = await SubjectAsync();

        await Assert.That(await s.Client.Source.GetBranchHeadShaAsync(Binding()))
            .SucceededAndValue(sha => sha.IsEqualTo(s.Client.BranchHead));
    }

    [Test]
    public async Task Fetch_WithoutAnETag_IsChanged_AtTheConfigCommit()
    {
        Rule();
        await using var s = await SubjectAsync();

        var changed = await ChangedAsync(s.Client, etag: null);

        await Assert.That(changed.Sha).IsEqualTo(s.Client.ConfigSha);
    }

    [Test]
    public async Task Fetch_WithTheETagItGave_IsNotModified()
    {
        Rule();
        await using var s = await SubjectAsync();
        var changed = await ChangedAsync(s.Client, etag: null);

        var fetch = await s.Client.Source.FetchConfigAsync(Binding(), changed.ETag);

        await Assert.That(fetch).SucceededAndValue(f => f.IsEqualTo(new ConfigFetch.NotModified(changed.ETag)));
    }

    [Test]
    public async Task Fetch_WithAnETagItNeverGave_IsChanged_AtTheConfigCommit()
    {
        Rule();
        await using var s = await SubjectAsync();

        var changed = await ChangedAsync(s.Client, etag: "stale");

        await Assert.That(changed.Sha).IsEqualTo(s.Client.ConfigSha);
    }
}
