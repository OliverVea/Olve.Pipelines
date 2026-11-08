using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Sync.ConfigSource;
using Olve.Pipelines.RealSystemTests.Contracts;
using Olve.Pipelines.Recorder.Recordings;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests.Recordings;

/// <summary>GitHubConfigSource against what GitHub really answered.</summary>
public class RecordedGitHubTests
{
    private static readonly string[] Names =
    [
        "branch-head", "config-cursor", "config-cursor-not-modified", "dependency-cursor", "dependency-cursor-not-modified", "package-json",
    ];

    private static readonly IReadOnlyList<Recording> Exchanges = Names.Select(n => RecordingReplay.Get("github", n)).ToList();
    private static readonly Recording NoUserAgent = RecordingReplay.Get("github", "no-user-agent");

    // GitHub refuses a request without a User-Agent, so the replay does too.
    internal static LoopbackHttpServer GitHub() => LoopbackHttpServer.Start(head =>
        RecordingReplay.Respond(
            RecordingReplay.Header(head, "User-Agent") is null
                ? NoUserAgent
                : RecordingReplay.Match(Exchanges, head) ?? throw new InvalidOperationException($"nothing recorded for {head}")));

    internal static GitHubConfigSource Source(LoopbackHttpServer server, IKubernetesClient? kubernetes = null) => new(
        kubernetes!,
        new KubernetesOptions("ns"),
        new ConfigurationBuilder().AddInMemoryCollection([KeyValuePair.Create<string, string?>("GitHub:ApiUrl", $"http://127.0.0.1:{server.Port}")]).Build(),
        NullLogger<GitHubConfigSource>.Instance);

    private static PipelineConfigBinding Binding(string? credentials = null) => new(
        Id.New<PipelineConfigBinding>(), Id.New<Pipeline>(), "OliverVea/Olve.Pipelines", "main", ".pipelines", credentials, null, null,
        ReconcileStatus.NeverRun, DateTimeOffset.UnixEpoch);

    internal static string Sha(string name)
    {
        var body = RecordingReplay.Get("github", name).Response.Body!;
        return (body is System.Text.Json.Nodes.JsonArray list ? list[0]! : body)["sha"]!.GetValue<string>();
    }

    internal static string HeadMessage() =>
        RecordingReplay.Get("github", "branch-head").Response.Body!["commit"]!["message"]!.GetValue<string>();

    [Test]
    public async Task EveryRequest_IsTheOneThatWasRecorded()
    {
        await using var server = GitHub();
        var source = Source(server);
        var binding = Binding();

        TestRunDomain.Pick(await source.GetBranchHeadAsync(binding));
        var first = (ConfigFetch.Changed)TestRunDomain.Pick(await source.FetchConfigAsync(binding));
        TestRunDomain.Pick(await source.FetchConfigAsync(binding, first.ETag));

        var answered = server.Requests.Select(head => (Head: head, Recording: RecordingReplay.Match(Exchanges, head)!)).ToList();
        await Assert.That(answered.Select(a => a.Recording.Name)).IsEquivalentTo(Names);
        foreach (var (head, recording) in answered)
        {
            foreach (var (name, value) in recording.Request.Headers)
                await Assert.That(RecordingReplay.Header(head, name)).IsEqualTo(value);
            await Assert.That(RecordingReplay.Header(head, "Authorization")).IsNull();
        }
    }

    [Test]
    public async Task APrivateRepo_SendsItsToken_AsABearer()
    {
        await using var server = GitHub();
        var kubernetes = new PipelineSecretCopyServiceTests.FakeSecrets();
        var binding = Binding("GITHUB_TOKEN");
        kubernetes.Secrets[PipelineSecrets.Name(binding.PipelineId)] = new Dictionary<string, string> { ["GITHUB_TOKEN"] = "the-token" };
        var source = Source(server, kubernetes);

        TestRunDomain.Pick(await source.GetBranchHeadAsync(binding));
        TestRunDomain.Pick(await source.FetchConfigAsync(binding));

        await Assert.That(server.Requests).IsNotEmpty();
        foreach (var head in server.Requests)
            await Assert.That(RecordingReplay.Header(head, "Authorization")).IsEqualTo("Bearer the-token");
    }

    [Test]
    public async Task ARefusal_NamesTheBindingAndTheStatus()
    {
        await using var server = LoopbackHttpServer.Start(RecordingReplay.Respond(NoUserAgent));
        var source = Source(server);

        var result = await source.GetBranchHeadAsync(Binding());

        await Assert.That(result).Failed();
        result.TryPickProblems(out var problems, out _);
        await Assert.That(problems!.Single().Message).IsEqualTo("GitHub returned 403 fetching branch head for 'OliverVea/Olve.Pipelines@main'.");
    }
}

[InheritsTests]
public class RecordedGitHubContract : ConfigSourceContract
{
    protected override Task<ContractSubject<Repository>> SubjectAsync()
    {
        var server = RecordedGitHubTests.GitHub();
        var repository = new Repository(RecordedGitHubTests.Source(server), RecordedGitHubTests.Sha("branch-head"), RecordedGitHubTests.HeadMessage(), RecordedGitHubTests.Sha("dependency-cursor"));
        return Task.FromResult(new ContractSubject<Repository>(repository, "", () => "", server));
    }
}
