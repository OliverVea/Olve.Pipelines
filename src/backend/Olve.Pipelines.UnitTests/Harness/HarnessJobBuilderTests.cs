using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests.Harness;

public class HarnessJobBuilderTests
{
    internal static readonly Id<Pipeline> PipelineId = new(new Id(new Guid("11111111-2222-3333-4444-555555555555")));
    internal static readonly Id<Attempt> AttemptId = new(new Id(new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")));
    internal static readonly Id<Run> RunId = new(new Id(new Guid("99999999-8888-7777-6666-555555555555")));

    internal static readonly HarnessOptions Options = new() { RuntimeClassName = "gvisor" };

    internal static HarnessJobRequest Request(
        IReadOnlyList<string>? secrets = null,
        IReadOnlyDictionary<string, string>? fileUrls = null,
        long? timeout = 600,
        string? inputUrl = "http://garage/input?sig") => new(
        JobName: "olve-aaaaaaaabbbbccccddddeeeeeeeeeeee",
        AttemptId: AttemptId,
        PipelineId: PipelineId,
        RunId: RunId,
        RunNumber: 412,
        StepName: "deploy-beta",
        AttemptNumber: 2,
        VersionStamp: "2026.10.21.412",
        StepImage: "alpine:3.22",
        TimeoutSeconds: timeout,
        Secrets: secrets ?? ["SSH_KEY", "KUBECONFIG"],
        PipelineSecretName: PipelineSecrets.Name(PipelineId),
        Urls: new HarnessUrls("http://garage/step?sig", inputUrl, "http://garage/result?sig",
            fileUrls ?? new Dictionary<string, string> { ["412/build/1/chart/"] = "http://garage/chart.tar?sig" }));

    private const string GoldenStepJob = """
        {
          "apiVersion": "batch/v1",
          "kind": "Job",
          "metadata": {
            "name": "olve-aaaaaaaabbbbccccddddeeeeeeeeeeee",
            "labels": {
              "app": "olve-pipelines",
              "job-name": "olve-aaaaaaaabbbbccccddddeeeeeeeeeeee",
              "olve-role": "step",
              "olve-pipeline": "11111111222233334444555555555555",
              "olve-run": "412",
              "olve-step": "deploy-beta",
              "olve-attempt": "2"
            }
          },
          "spec": {
            "template": {
              "metadata": {
                "name": "olve-aaaaaaaabbbbccccddddeeeeeeeeeeee",
                "labels": {
                  "app": "olve-pipelines",
                  "job-name": "olve-aaaaaaaabbbbccccddddeeeeeeeeeeee",
                  "olve-role": "step",
                  "olve-pipeline": "11111111222233334444555555555555",
                  "olve-run": "412",
                  "olve-step": "deploy-beta",
                  "olve-attempt": "2"
                }
              },
              "spec": {
                "containers": [
                  {
                    "name": "step",
                    "image": "alpine:3.22",
                    "command": ["/olve/lib/ld-linux-x86-64.so.2", "--library-path", "/olve/lib", "/olve/step", "run", "deploy-beta"],
                    "env": [
                      { "name": "OLVE_DIR", "value": "/olve" },
                      { "name": "TMPDIR", "value": "/olve/tmp" },
                      { "name": "OLVE_PIPELINE", "value": "11111111-2222-3333-4444-555555555555" },
                      { "name": "OLVE_RUN_ID", "value": "99999999-8888-7777-6666-555555555555" },
                      { "name": "OLVE_RUN", "value": "412" },
                      { "name": "OLVE_ATTEMPT", "value": "2" },
                      { "name": "OLVE_VERSION_STAMP", "value": "2026.10.21.412" },
                      { "name": "OLVE_RESULT_URL", "valueFrom": { "secretKeyRef": { "name": "olve-urls-aaaaaaaabbbbccccddddeeeeeeeeeeee", "key": "OLVE_RESULT_URL", "optional": false } } },
                      { "name": "OLVE_FILE_URLS", "valueFrom": { "secretKeyRef": { "name": "olve-urls-aaaaaaaabbbbccccddddeeeeeeeeeeee", "key": "OLVE_FILE_URLS", "optional": false } } },
                      { "name": "KUBECONFIG", "valueFrom": { "secretKeyRef": { "name": "olve-pipeline-11111111222233334444555555555555", "key": "KUBECONFIG", "optional": true } } },
                      { "name": "SSH_KEY", "valueFrom": { "secretKeyRef": { "name": "olve-pipeline-11111111222233334444555555555555", "key": "SSH_KEY", "optional": true } } }
                    ],
                    "volumeMounts": [{ "name": "olve", "mountPath": "/olve" }],
                    "securityContext": { "allowPrivilegeEscalation": false }
                  }
                ],
                "initContainers": [
                  {
                    "name": "olve-init",
                    "image": "ghcr.io/olivervea/olve-pipelines-harness-base:src-1559dbe086e1",
                    "env": [
                      { "name": "OLVE_STEP_URL", "valueFrom": { "secretKeyRef": { "name": "olve-urls-aaaaaaaabbbbccccddddeeeeeeeeeeee", "key": "OLVE_STEP_URL", "optional": false } } },
                      { "name": "OLVE_INPUT_URL", "valueFrom": { "secretKeyRef": { "name": "olve-urls-aaaaaaaabbbbccccddddeeeeeeeeeeee", "key": "OLVE_INPUT_URL", "optional": false } } }
                    ],
                    "volumeMounts": [{ "name": "olve", "mountPath": "/shared" }],
                    "securityContext": { "allowPrivilegeEscalation": false },
                    "imagePullPolicy": "IfNotPresent",
                    "resources": { "requests": { "cpu": "100m", "memory": "64Mi" }, "limits": { "memory": "256Mi" } }
                  }
                ],
                "volumes": [{ "name": "olve", "emptyDir": { "sizeLimit": "1Gi" } }],
                "restartPolicy": "Never",
                "runtimeClassName": "gvisor",
                "securityContext": { "seccompProfile": { "type": "RuntimeDefault" } },
                "automountServiceAccountToken": false,
                "enableServiceLinks": false
              }
            },
            "backoffLimit": 0,
            "ttlSecondsAfterFinished": 604800,
            "activeDeadlineSeconds": 720
          }
        }
        """;

    [Test]
    public async Task StepJob_MatchesGolden()
    {
        var spec = HarnessJobBuilder.Build(Request(), Options);

        await Assert.That(spec).Succeeded();
        await AssertJsonEqual(Serialize(TestRunDomain.Pick(spec).Job), GoldenStepJob);
    }

    [Test]
    public async Task StepJob_UrlsLiveOnlyInTheSecret()
    {
        var spec = TestRunDomain.Pick(HarnessJobBuilder.Build(Request(), Options));
        var json = Serialize(spec.Job);

        await Assert.That(json).DoesNotContain("garage");
        await Assert.That(spec.UrlSecretName).IsEqualTo("olve-urls-aaaaaaaabbbbccccddddeeeeeeeeeeee");
        await Assert.That(spec.UrlSecretData["OLVE_STEP_URL"]).IsEqualTo("http://garage/step?sig");
        await Assert.That(spec.UrlSecretData["OLVE_INPUT_URL"]).IsEqualTo("http://garage/input?sig");
        await Assert.That(spec.UrlSecretData["OLVE_RESULT_URL"]).IsEqualTo("http://garage/result?sig");
        await Assert.That(spec.UrlSecretData["OLVE_FILE_URLS"]).IsEqualTo("""{"412/build/1/chart/":"http://garage/chart.tar?sig"}""");
    }

    [Test]
    public async Task StepJob_NoInputNoFiles_OmitsThoseEnvVars()
    {
        var spec = TestRunDomain.Pick(HarnessJobBuilder.Build(Request(inputUrl: null, fileUrls: new Dictionary<string, string>(), secrets: []), Options));
        var json = Serialize(spec.Job);

        await Assert.That(json).DoesNotContain("OLVE_INPUT_URL");
        await Assert.That(json).DoesNotContain("OLVE_FILE_URLS");
        await Assert.That(json).DoesNotContain("olve-pipeline-");
        await Assert.That(json).DoesNotContain("envFrom");
        await Assert.That(spec.UrlSecretData.Keys.Order().ToArray()).IsEquivalentTo(new[] { "OLVE_RESULT_URL", "OLVE_STEP_URL" });
    }

    [Test]
    public async Task StepJob_Deadline_DefaultsAndCaps()
    {
        var noTimeout = TestRunDomain.Pick(HarnessJobBuilder.Build(Request(timeout: null), Options));
        var huge = TestRunDomain.Pick(HarnessJobBuilder.Build(Request(timeout: 999_999), Options));

        await Assert.That(noTimeout.Job.Spec.ActiveDeadlineSeconds).IsEqualTo(3600 + 120);
        await Assert.That(huge.Job.Spec.ActiveDeadlineSeconds).IsEqualTo(6 * 3600 + 120);
    }

    [Test]
    public async Task StepJob_NoRuntimeClass_OmitsIt()
    {
        var spec = TestRunDomain.Pick(HarnessJobBuilder.Build(Request(), Options with { RuntimeClassName = null }));

        await Assert.That(Serialize(spec.Job)).DoesNotContain("runtimeClassName");
    }

    [Test]
    public async Task StepJob_ClusterAccess_GetsTheStepServiceAccount_OnlyWhenTheInstanceGrantsOne()
    {
        var granted = Options with { StepServiceAccount = "olve-deployer" };
        var asks = Request() with { ClusterAccess = true };

        var withToken = TestRunDomain.Pick(HarnessJobBuilder.Build(asks, granted)).Job.Spec.Template.Spec;
        var notGranted = TestRunDomain.Pick(HarnessJobBuilder.Build(asks, Options)).Job.Spec.Template.Spec;
        var notAsked = TestRunDomain.Pick(HarnessJobBuilder.Build(Request(), granted)).Job.Spec.Template.Spec;

        await Assert.That(withToken.ServiceAccountName).IsEqualTo("olve-deployer");
        await Assert.That(withToken.AutomountServiceAccountToken).IsTrue();
        await Assert.That(notGranted.ServiceAccountName).IsNull();
        await Assert.That(notGranted.AutomountServiceAccountToken).IsFalse();
        await Assert.That(notAsked.ServiceAccountName).IsNull();
        await Assert.That(notAsked.AutomountServiceAccountToken).IsFalse();
    }

    [Test]
    [Arguments("100m", "256Mi", "1Gi")]
    [Arguments("100m", null, null)]
    [Arguments(null, "256Mi", null)]
    [Arguments(null, null, "1Gi")]
    public async Task StepJob_CarriesTheConfiguredResources(string? cpu, string? memory, string? limit)
    {
        var options = Options with { StepCpuRequest = cpu, StepMemoryRequest = memory, StepMemoryLimit = limit };

        var resources = TestRunDomain.Pick(HarnessJobBuilder.Build(Request(), options)).Job.Spec.Template.Spec.Containers[0].Resources!;

        await Assert.That(resources.Requests?.GetValueOrDefault("cpu")).IsEqualTo(cpu);
        await Assert.That(resources.Requests?.GetValueOrDefault("memory")).IsEqualTo(memory);
        await Assert.That(resources.Limits?.GetValueOrDefault("memory")).IsEqualTo(limit);
    }

    // Catches: TTL 0 deleting a map-trigger Job before its log is read, failing every automatic trigger,
    // and caps small enough to refuse normal artifacts.
    [Test]
    public async Task Defaults_KeepMapTriggerJobsADay_AndAcceptLargeArtifacts()
    {
        var options = new HarnessOptions();
        var job = TestRunDomain.Pick(HarnessJobBuilder.BuildMapTrigger(new MapTriggerJobRequest("olve-map-abc", PipelineId, "push", "http://g/step", "http://g/event"), options)).Job;

        await Assert.That(job.Spec.TtlSecondsAfterFinished).IsEqualTo(86_400);
        await Assert.That(options.ResultLimits.MaxArchiveBytes).IsEqualTo(2L << 30);
        await Assert.That(options.ResultLimits.MaxTotalBytes).IsEqualTo(4L << 30);
    }

    [Test]
    [Arguments("gvisor", "gvisor")]
    [Arguments("  ", null)]
    [Arguments(null, null)]
    public async Task RuntimeClassName_ComesFromKubernetesConfiguration(string? configured, string? expected)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Configuration.AddInMemoryCollection([new("Kubernetes:RuntimeClassName", configured)]);

        builder.ConfigureKubernetes();

        await Assert.That(builder.Services.BuildServiceProvider().GetRequiredService<HarnessOptions>().RuntimeClassName).IsEqualTo(expected);
    }

    internal static string Serialize(K8sJobManifest manifest)
        => JsonSerializer.Serialize(manifest, HarnessKubernetesJsonContext.Default.K8sJobManifest);

    internal static async Task AssertJsonEqual(string actual, string expected)
    {
        var equal = JsonNode.DeepEquals(JsonNode.Parse(actual), JsonNode.Parse(expected));
        if (!equal)
            Console.WriteLine($"Actual JSON:\n{actual}");
        await Assert.That(equal).IsTrue();
    }
}
