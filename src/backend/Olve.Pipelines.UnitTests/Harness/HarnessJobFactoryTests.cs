using System.Text.Json;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Shared.Storage;
using Olve.Pipelines.UnitTests.Manifests;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests.Harness;

/// <summary>The default options give a step 120 s to start, and its URLs 600 s more after that.</summary>
public class HarnessJobFactoryTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string Files = $$"""
        {"files":[{"run":1,"step":"build","attempt":1,"path":"a.tar","sha256":"{{Sha}}"},
                  {"run":1,"step":"build","attempt":1,"path":"b.tar","sha256":"{{Sha}}"}]}
        """;

    private static readonly Attempt Attempt = new(Id.New<Attempt>(), HarnessJobBuilderTests.PipelineId, HarnessJobBuilderTests.RunId, 7, "hello", 2, new AttemptStatus.Scheduled(), DateTimeOffset.UnixEpoch);

    private static HarnessJobFactory Factory() => new(new S3UrlPresigner(
        new PresignOptions(new Uri("http://garage.test:3900"), "bucket", "us-east-1", TimeSpan.FromHours(1)),
        new StubS3CredentialsProvider(), TimeProvider.System), new HarnessOptions());

    private static StepDescriptor Step()
    {
        RunPlan.From(TestFixtureManifest.Parse()).TryGetStep("hello", out _, out var step);
        return step! with { TimeoutSeconds = 600 };
    }

    [Test]
    public async Task AStepJob_CarriesItsUrlsAndCases()
    {
        var input = JsonDocument.Parse(Files).RootElement;

        var spec = TestRunDomain.Pick(await Factory().CreateStepJobAsync(Attempt, "versions/p/builds/x/step.tar.gz", "2026.11.01.7", Step(), input,
            ["target.kind=ssh"], new Dictionary<string, string> { ["decision"] = "https://p.example/cb" }));

        var urls = spec.UrlSecretData;
        await Assert.That(urls[HarnessJobBuilder.StepUrlKey]).Contains("versions/p/builds/x/step.tar.gz?");
        await Assert.That(urls[HarnessJobBuilder.InputUrlKey]).Contains(HarnessStorageKeys.AttemptInputKey(Attempt));
        await Assert.That(urls[HarnessJobBuilder.ResultUrlKey]).Contains(HarnessStorageKeys.AttemptResultArchiveKey(Attempt));
        await Assert.That(JsonSerializer.Deserialize<Dictionary<string, string>>(urls[HarnessJobBuilder.FileUrlsKey])!.Keys).IsEquivalentTo(["1/build/1/a.tar", "1/build/1/b.tar"]);
        await Assert.That(urls[HarnessJobBuilder.CallbackUrlsKey]).Contains("https://p.example/cb");
        await Assert.That(spec.Job.Spec.Template.Spec.Containers[0].Env!.Single(e => e.Name == HarnessJobBuilder.CasesKey).Value).IsEqualTo("""["target.kind=ssh"]""");
    }

    [Test]
    public async Task AStepWithoutInput_HasNoInputOrFileUrls_NorCases()
    {
        var spec = TestRunDomain.Pick(await Factory().CreateStepJobAsync(Attempt, "versions/p/builds/x/step.tar.gz", "2026.11.01.7", Step(), input: null));

        await Assert.That(spec.UrlSecretData.Keys).IsEquivalentTo([HarnessJobBuilder.StepUrlKey, HarnessJobBuilder.ResultUrlKey]);
        await Assert.That(spec.Job.Spec.Template.Spec.Containers[0].Env!.Select(e => e.Name)).DoesNotContain(HarnessJobBuilder.CasesKey);
    }

    // Catches: a URL that expires before the step's deadline, so a long step's result PUT gets a 403 at the very end.
    [Test]
    public async Task UrlsUsedLate_OutliveTheDeadlineByTheGrace()
    {
        var input = JsonDocument.Parse(Files).RootElement;
        const string Expiry = "X-Amz-Expires=1320&"; // 600 s timeout + 120 s startup + 600 s grace

        var urls = TestRunDomain.Pick(await Factory().CreateStepJobAsync(Attempt, "k", "s", Step(), input)).UrlSecretData;

        await Assert.That(urls[HarnessJobBuilder.ResultUrlKey]).Contains(Expiry);
        await Assert.That(urls[HarnessJobBuilder.FileUrlsKey]).Contains(Expiry);
    }

    [Test]
    public async Task AMapTriggerJobsUrls_OutliveItsDeadlineByTheGrace()
    {
        var spec = TestRunDomain.Pick(await Factory().CreateMapTriggerJobAsync("olve-map-a", HarnessJobBuilderTests.PipelineId, "k", "push", "e"));

        // 300 s deadline + 600 s grace
        await Assert.That(spec.UrlSecretData[HarnessJobBuilder.StepUrlKey]).Contains("X-Amz-Expires=900&");
        await Assert.That(spec.UrlSecretData[HarnessJobBuilder.InputUrlKey]).Contains("X-Amz-Expires=900&");
    }
}
