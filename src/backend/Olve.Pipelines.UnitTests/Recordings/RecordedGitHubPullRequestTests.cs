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
        new[] { "pulls-open", "pull", "pull-unknown" }.Select(n => RecordingReplay.Get("github", n)).ToList();

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
        await Assert.That(TestRunDomain.Pick(result)).IsEmpty();
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
        await Assert.That(pull.MergedAt is not null).IsEqualTo(recorded["merged_at"] is not null);
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
