using System.Net;
using System.Text.Json.Nodes;
using Olve.Pipelines.Api;

namespace Olve.Pipelines.RealSystemTests.Flows;

public class PipelineLifecycleFlowTests
{
    /// <summary>Thresholds on the test host: orphans within a minute or two, binaries within about three.</summary>
    private static readonly TimeSpan CleanupBudget = TimeSpan.FromMinutes(5);

    [Test]
    public async Task Pipeline_FromCreateToDelete_LeavesNothingBehind()
    {
        await using var server = await TestServer.LeaseAsync();

        var pipeline = await server.BindAsync("flow");
        await server.Api.SetSecretAsync(pipeline.Id, "FIXTURE_SECRET", "s3cret");
        await Assert.That(await server.Api.ReconcileAsync(pipeline.Id)).IsEqualTo(new ApiReply(HttpStatusCode.OK, null));
        await Assert.That((await server.Api.GetPipelineAsync(pipeline.Id)).Name).IsEqualTo(pipeline.Name);

        var run = await server.StartRunAsync(pipeline, new JsonObject { ["message"] = "through the file ref" });
        var finished = await server.WaitForRunAsync(pipeline, run.Number);

        await Assert.That(finished.Status).IsEqualTo(RunStatus.Succeeded);
        await Assert.That(await server.Api.GetStepFileAsync(pipeline.Id, run.Number, "produce", "message.txt")).IsEqualTo("through the file ref");
        var output = JsonNode.Parse(await server.Api.GetStepOutputAsync(pipeline.Id, run.Number, "consume"))!;
        await Assert.That(output.ToJsonString()).Contains("through the file ref");
        await Assert.That(output.ToJsonString()).Contains("\"secretLength\":6");
        await Assert.That(await server.Api.GetStepLogsAsync(pipeline.Id, run.Number, "produce")).Contains("produced the message");

        var hex = pipeline.Id.ToString("N");
        var secret = TestCluster.PipelineSecretName(pipeline.Id);
        var cluster = server.Cluster;
        await Assert.That(await cluster.JobsOfAsync(pipeline.Id)).IsNotEmpty();
        await Assert.That(await cluster.Client.GetSecretAsync(cluster.Namespace, secret)).IsNotNull();
        await Assert.That(await server.Bucket.ListAsync($"runs/{hex}/")).IsNotEmpty();
        await Assert.That(await server.Bucket.ListAsync($"versions/{hex}/")).IsNotEmpty();

        var jobs = await cluster.JobsOfAsync(pipeline.Id);

        await Assert.That((await server.Api.DeletePipelineAsync(pipeline.Id)).Status).IsEqualTo(HttpStatusCode.OK);

        var gone = await server.Api.GetPipelineReplyAsync(pipeline.Id);
        await Assert.That(gone.Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(gone.Detail).IsEqualTo($"Pipeline '{pipeline.Id}' not found.");

        var leftovers = await Wait.TryUntilAsync(() => StoredLeftoversAsync(server, pipeline.Id, jobs), l => l.Count == 0, CleanupBudget);
        await Assert.That(leftovers).IsEmpty();
        await Assert.That(await cluster.JobsOfAsync(pipeline.Id)).IsEmpty();
    }

    private static async Task<IReadOnlyList<string>> StoredLeftoversAsync(TestServer server, Guid pipelineId, IReadOnlyList<string> jobs)
    {
        var hex = pipelineId.ToString("N");
        var cluster = server.Cluster;
        var urlSecrets = jobs.Select(j => j.StartsWith("olve-build-", StringComparison.Ordinal) ? $"{j}-urls" : j.Replace("olve-", "olve-urls-", StringComparison.Ordinal));
        var names = urlSecrets.Append(TestCluster.PipelineSecretName(pipelineId)).ToHashSet();

        var left = (await cluster.Client.ListSecretsAsync(cluster.Namespace))
            .Where(s => names.Contains(s.Name)).Select(s => $"secret {s.Name}").ToList();
        foreach (var prefix in new[] { $"runs/{hex}/", $"versions/{hex}/", $"config/{hex}/", $"triggers/{hex}/" })
            left.AddRange((await server.Bucket.ListAsync(prefix)).Select(o => $"object {o.Key}"));
        return left;
    }

    // Parallel first sets race to create the Secret, and later ones to update it.
    [Test]
    public async Task ParallelSets_OfDifferentSecrets_KeepEveryValue()
    {
        await using var server = await TestServer.LeaseAsync();
        var pipeline = await server.NewPipelineAsync("secrets");
        var cluster = server.Cluster;

        foreach (var round in new[] { "first", "second" })
        {
            var names = Enumerable.Range(0, 8).Select(i => $"{round.ToUpperInvariant()}_{i}").ToList();

            await Task.WhenAll(names.Select(n => server.Api.SetSecretAsync(pipeline.Id, n, n)));

            var stored = await cluster.Client.GetSecretAsync(cluster.Namespace, TestCluster.PipelineSecretName(pipeline.Id));
            foreach (var name in names)
                await Assert.That(stored!.GetValueOrDefault(name)).IsEqualTo(name);
        }
    }

    [Test]
    public async Task FailingStep_FailsTheRun_WithTheHandlersMessage()
    {
        await using var server = await TestServer.LeaseAsync();
        var pipeline = await server.NewPipelineAsync("fail");
        await server.Api.SetSecretAsync(pipeline.Id, "FIXTURE_SECRET", "x");

        var run = await server.StartRunAsync(pipeline, new JsonObject { ["outcome"] = "fail" });
        var finished = await server.WaitForRunAsync(pipeline, run.Number);

        await Assert.That(finished.Status).IsEqualTo(RunStatus.Failed);
        var step = finished.Stages[1].Steps.Single(s => s.Name == "consume");
        await Assert.That(step.Reason).IsEqualTo("the handler threw (exit 1): step 'consume' failed: the fixture was asked to fail");
    }

    [Test]
    public async Task DeclaredSecretThatIsUnset_FailsTheStep_NamingTheSecret()
    {
        await using var server = await TestServer.LeaseAsync();
        var pipeline = await server.NewPipelineAsync("secret");

        var run = await server.StartRunAsync(pipeline);
        var finished = await server.WaitForRunAsync(pipeline, run.Number);

        await Assert.That(finished.Status).IsEqualTo(RunStatus.Failed);
        var step = finished.Stages[1].Steps.Single(s => s.Name == "consume");
        await Assert.That(step.Reason).IsEqualTo(
            "the handler threw (exit 1): step 'consume' failed: secret FIXTURE_SECRET is not set (the harness reads it from the environment variable FIXTURE_SECRET)");
    }

    [Test]
    public async Task InvalidInput_StartsNoRun_AndSaysWhy()
    {
        await using var server = await TestServer.LeaseAsync();
        var pipeline = await server.NewPipelineAsync("input");

        var reply = await server.Api.StartRunReplyAsync(pipeline.Id, new JsonObject { ["outcome"] = "sideways" });

        await Assert.That(reply).IsEqualTo(new ApiReply(HttpStatusCode.BadRequest, "must be one of [\"succeed\",\"fail\"]", "input.outcome"));
    }

    [Test]
    public async Task NewCommit_ReconcilesToANewVersion_AndABrokenOneKeepsTheOld()
    {
        await using var server = await TestServer.LeaseAsync();
        var pipeline = await server.NewPipelineAsync("commit");
        const string path = ".pipelines/pipeline.ts";
        var fixture = await File.ReadAllTextAsync(Path.Combine(TestTargets.RepoRoot(), "tools/real-system/fixture", path));

        await server.GitHub.CommitAsync(pipeline.Repo, new Dictionary<string, string>
        {
            [path] = fixture.Replace("__PIPELINE_NAME__", pipeline.Name).Replace("Real-system test fixture.", "Changed by a commit."),
        });
        await Assert.That((await server.Api.ReconcileAsync(pipeline.Id)).Status).IsEqualTo(HttpStatusCode.OK);

        var versions = await server.Api.ListVersionsAsync(pipeline.Id);
        await Assert.That(versions.Versions.Select(v => v.Number)).IsEquivalentTo([2, 1]);
        await Assert.That((await server.Api.GetPipelineAsync(pipeline.Id)).Description).IsEqualTo("Changed by a commit.");

        await server.GitHub.CommitAsync(pipeline.Repo, new Dictionary<string, string> { [path] = "this is not TypeScript (" });
        var broken = await server.Api.ReconcileAsync(pipeline.Id);

        await Assert.That(broken.Status).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(broken.Detail).Contains("Build/describe failed");
        await Assert.That((await server.Api.ListVersionsAsync(pipeline.Id)).Versions.Count).IsEqualTo(2);
    }
}
