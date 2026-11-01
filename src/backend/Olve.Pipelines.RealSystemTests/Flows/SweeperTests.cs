namespace Olve.Pipelines.RealSystemTests.Flows;

public class SweeperTests
{
    [Test]
    public async Task Sweep_RemovesOnlyWhatMatchesThePrefixAndTheAge()
    {
        await using var server = await TestServer.LeaseAsync();
        var cluster = server.Cluster;
        var sweeper = new Sweeper(server.Api, cluster, server.Bucket, TimeProvider.System);
        var scope = TestServer.NewName("sweep");

        var binding = await server.Api.CreatePipelineAsync($"e2e/{scope}-pipeline");
        var other = await server.BindAsync("sweep-other");
        await cluster.Client.CreateSecretAsync(cluster.Namespace, $"{scope}-secret", new() { ["A"] = "1" });
        var job = Kubernetes.TestJobs.WithInit($"{scope}-job", "true");
        await cluster.Client.CreateJobAsync(cluster.Namespace, job);
        await server.Bucket.PutAsync($"{scope}/object", "x");

        var young = await sweeper.SweepAsync(scope, TimeSpan.FromHours(1));

        await Assert.That(young.Pipelines.Concat(young.Secrets).Concat(young.Jobs).Concat(young.Objects)).IsEmpty();

        var swept = await sweeper.SweepAsync(scope, TimeSpan.Zero);

        await Assert.That(swept.Pipelines).IsEquivalentTo([$"{scope}-pipeline"]);
        await Assert.That(swept.Secrets).IsEquivalentTo([$"{scope}-secret"]);
        await Assert.That(swept.Jobs).IsEquivalentTo([$"{scope}-job"]);
        await Assert.That(swept.Objects).IsEquivalentTo([$"{scope}/object"]);
        await Assert.That((await server.Api.GetPipelineReplyAsync(binding.PipelineId)).Status).IsEqualTo(System.Net.HttpStatusCode.NotFound);
        await Assert.That((await server.Api.GetPipelineReplyAsync(other.Id)).Status).IsEqualTo(System.Net.HttpStatusCode.OK);
    }

    [Test]
    public async Task Sweep_RefusesAPrefixOutsideTheTestNames()
    {
        await using var server = await TestServer.LeaseAsync();
        var sweeper = new Sweeper(server.Api, server.Cluster, server.Bucket, TimeProvider.System);

        var thrown = await Assert.That(async () => await sweeper.SweepAsync("olve-", TimeSpan.Zero)).Throws<ArgumentException>();

        await Assert.That(thrown!.Message).StartsWith("The sweeper only removes names starting with 'e2e-', not 'olve-'.");
    }
}
