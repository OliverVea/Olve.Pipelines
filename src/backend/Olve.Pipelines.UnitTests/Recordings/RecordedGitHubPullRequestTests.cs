using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.GitHub;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Ci;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests.Recordings;

/// <summary>GitHubPullRequestSource and the pull request shape against what GitHub really answered.</summary>
public class RecordedGitHubPullRequestTests
{
    private static readonly IReadOnlyList<Recorder.Recordings.Recording> Exchanges =
        new[] { "pulls-open", "pull", "pull-unknown", "git-commit", "compare-files" }.Select(n => RecordingReplay.Get("github", n)).ToList();

    private static LoopbackHttpServer GitHub() => LoopbackHttpServer.Start(head =>
        RecordingReplay.Respond(RecordingReplay.Match(Exchanges, head) ?? throw new InvalidOperationException($"nothing recorded for {head}")));

    private static GitHubPullRequestSource Source(LoopbackHttpServer server) => new(
        null!,
        new KubernetesOptions("ns"),
        new ConfigurationBuilder().AddInMemoryCollection([KeyValuePair.Create<string, string?>("GitHub:ApiUrl", $"http://127.0.0.1:{server.Port}")]).Build(),
        NullLogger<GitHubPullRequestSource>.Instance);

    private static PipelineConfigBinding Binding() => new(
        Id.New<PipelineConfigBinding>(), Id.New<Pipeline>(), "OliverVea/Olve.Pipelines", "main", ".pipelines", null, null, null,
        ReconcileStatus.NeverRun, DateTimeOffset.UnixEpoch);

    private static int RecordedNumber() => RecordingReplay.Get("github", "pull").Response.Body!["number"]!.GetValue<int>();

    [Test]
    public async Task Listing_the_open_pull_requests_asks_for_the_binding_branch()
    {
        await using var server = GitHub();

        var result = await Source(server).ListOpenAsync(Binding());

        await Assert.That(result).Succeeded();
        var recorded = RecordingReplay.Get("github", "pulls-open").Response.Body!.AsArray();
        await Assert.That(TestRunDomain.Pick(result).Select(p => (p.Number, p.User?.Login)))
            .IsEquivalentTo(recorded.Select(p => (p!["number"]!.GetValue<int>(), (string?)p["user"]!["login"]!.GetValue<string>())));
    }

    [Test]
    public async Task A_pull_request_reads_as_GitHub_reported_it()
    {
        await using var server = GitHub();
        var recorded = RecordingReplay.Get("github", "pull").Response.Body!;

        var result = await Source(server).GetAsync(Binding(), RecordedNumber());

        await Assert.That(result).Succeeded();
        var pull = TestRunDomain.Pick(result)!;
        await Assert.That(pull.Number).IsEqualTo(RecordedNumber());
        await Assert.That(pull.Title).IsEqualTo(recorded["title"]!.GetValue<string>());
        await Assert.That(pull.Head.Sha).IsEqualTo(recorded["head"]!["sha"]!.GetValue<string>());
        await Assert.That(pull.Base.Ref).IsEqualTo("main");
        await Assert.That(pull.FromFork).IsFalse();
        await Assert.That(pull.User?.Login).IsEqualTo(recorded["user"]!["login"]!.GetValue<string>());
        await Assert.That(pull.MergedAt is not null).IsEqualTo(recorded["merged_at"] is not null);
        await Assert.That(pull.MergeCommitSha).IsEqualTo(recorded["merge_commit_sha"]!.GetValue<string>());
    }

    [Test]
    public async Task A_merge_commit_reads_as_its_tree_and_parents()
    {
        await using var server = GitHub();
        var recorded = RecordingReplay.Get("github", "git-commit").Response.Body!;

        var result = await Source(server).GetCommitAsync(Binding(), recorded["sha"]!.GetValue<string>());

        await Assert.That(result).Succeeded();
        var commit = TestRunDomain.Pick(result);
        await Assert.That(commit.Tree.Sha).IsEqualTo(recorded["tree"]!["sha"]!.GetValue<string>());
        await Assert.That(commit.Parents.Select(p => p.Sha)).IsEquivalentTo(recorded["parents"]!.AsArray().Select(p => p!["sha"]!.GetValue<string>()));
    }

    [Test]
    public async Task A_comparison_reads_as_the_paths_it_changes()
    {
        await using var server = GitHub();
        var exchange = RecordingReplay.Get("github", "compare-files");
        var range = exchange.Request.Url.Split("/compare/")[1].Split("...");

        var result = await Source(server).ChangedFilesAsync(Binding(), range[0], range[1]);

        await Assert.That(result).Succeeded();
        await Assert.That(TestRunDomain.Pick(result)).IsEquivalentTo(exchange.Response.Body!["files"]!.AsArray().Select(f => f!["filename"]!.GetValue<string>()));
    }

    [Test]
    public async Task A_pull_request_GitHub_doesnt_have_is_none()
    {
        await using var server = GitHub();

        await Assert.That(await Source(server).GetAsync(Binding(), 999999)).SucceededAndValue(v => v.IsNull());
    }

    [Test]
    public async Task Listed_pull_requests_read_like_single_ones()
    {
        var listed = RecordingReplay.Get("github", "pulls-listed").Response.Body!.Deserialize(GitHubPullRequestJsonContext.Default.GitHubPullRequestArray)!;

        await Assert.That(listed).IsNotEmpty();
        await Assert.That(listed.All(p => p is { Number: > 0, Title.Length: > 0, Head.Sha.Length: 40, Base.Ref: "main", FromFork: false, Mergeable: null })).IsTrue();
    }
}
