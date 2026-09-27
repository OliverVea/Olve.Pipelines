using Olve.Pipelines.Shared.Persistence;

namespace Olve.Pipelines.UnitTests;

public class BundleRetentionPolicyTests
{
    private static readonly DateTimeOffset Now = new(2027, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan SixMonths = TimeSpan.FromDays(183);

    private static string Hex(int n) => n.ToString("x32");

    private static StoredBundle Bundle(int pipeline, int bundle, int daysOld) =>
        new(Hex(pipeline), Hex(bundle), Now - TimeSpan.FromDays(daysOld), [$"bundles/{Hex(pipeline)}/{Hex(bundle)}/logs/x.log"]);

    [Test]
    public async Task OldBundles_WithinLatest20_AreKept()
    {
        // An idle pipeline: 20 bundles, all a year old — none may go.
        var bundles = Enumerable.Range(1, 20).Select(i => Bundle(1, i, 365 + i));

        var expired = BundleRetentionPolicy.SelectExpired(bundles, Now, keepLatest: 20, SixMonths);

        await Assert.That(expired).IsEmpty();
    }

    [Test]
    public async Task RecentBundles_Beyond20_AreKept()
    {
        // A busy pipeline: 50 bundles, all younger than 6 months — none may go.
        var bundles = Enumerable.Range(1, 50).Select(i => Bundle(1, i, i));

        var expired = BundleRetentionPolicy.SelectExpired(bundles, Now, keepLatest: 20, SixMonths);

        await Assert.That(expired).IsEmpty();
    }

    [Test]
    public async Task OnlyBundlesBothOldAndBeyond20_AreDeleted()
    {
        // 30 bundles, 10 days apart: newest 20 kept; of the older 10, only those past 183 days go.
        var bundles = Enumerable.Range(0, 30).Select(i => Bundle(1, i, i * 10)).ToList();

        var expired = BundleRetentionPolicy.SelectExpired(bundles, Now, keepLatest: 20, SixMonths);

        // Positions 20..29 are 200..290 days old: all past 183 days.
        await Assert.That(expired.Select(b => b.BundleKey)).IsEquivalentTo(Enumerable.Range(20, 10).Select(Hex));
    }

    [Test]
    public async Task Pipelines_AreRankedIndependently()
    {
        // Pipeline 1 has 25 old bundles (5 expire); pipeline 2 has 5 old bundles (all kept).
        var bundles = Enumerable.Range(1, 25).Select(i => Bundle(1, i, 300 + i))
            .Concat(Enumerable.Range(100, 5).Select(i => Bundle(2, i, 400)));

        var expired = BundleRetentionPolicy.SelectExpired(bundles, Now, keepLatest: 20, SixMonths);

        await Assert.That(expired).Count().IsEqualTo(5);
        await Assert.That(expired.All(b => b.PipelineKey == Hex(1))).IsTrue();
    }

    [Test]
    public async Task GroupObjects_UsesNewestObject_AndIgnoresForeignKeys()
    {
        var p = Hex(1);
        var b = Hex(2);
        var objects = new (string, DateTimeOffset)[]
        {
            ($"bundles/{p}/{b}/production/step/image.tar", Now.AddDays(-300)),
            ($"bundles/{p}/{b}/logs/job.log", Now.AddDays(-10)),
            ("bundles/artifact/something.json", Now.AddDays(-900)),
            ($"bundles/{p}/not-a-bundle-id/x", Now.AddDays(-900)),
            ("jobs.json", Now.AddDays(-900)),
        };

        var grouped = BundleRetentionPolicy.GroupObjects(objects);

        await Assert.That(grouped).Count().IsEqualTo(1);
        await Assert.That(grouped[0].LastActivity).IsEqualTo(Now.AddDays(-10));
        await Assert.That(grouped[0].ObjectKeys).Count().IsEqualTo(2);
    }
}
