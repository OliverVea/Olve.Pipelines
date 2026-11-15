using System.Text.Json.Nodes;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Kubernetes.Harness;

namespace Olve.Pipelines.UnitTests.Harness;

public class HelperNodesTests
{
    private static readonly HarnessOptions On = HarnessJobBuilderTests.Options with { HelperNodes = HelperNodes.Parse("olve.pro/role=helper", null) };

    public static IEnumerable<Func<(string Kind, Func<HarnessOptions, K8sJobManifest> Job)>> Jobs()
    {
        yield return () => ("step", o => TestRunDomain.Pick(HarnessJobBuilder.Build(HarnessJobBuilderTests.Request(), o)).Job);
        yield return () => ("preflight", o => TestRunDomain.Pick(HarnessJobBuilder.Build(
            HarnessJobBuilderTests.Request() with { Mode = HarnessJobBuilder.PreflightMode }, o)).Job);
        yield return () => ("build", o => TestRunDomain.Pick(BuildJobBuilder.Build(BuildJobBuilderTests.Request(), o)).Job);
    }

    private static JsonObject PodSpec(K8sJobManifest job) =>
        JsonNode.Parse(HarnessJobBuilderTests.Serialize(job))!["spec"]!["template"]!["spec"]!.AsObject();

    [Test]
    [MethodDataSource(nameof(Jobs))]
    public async Task Without_helper_nodes_a_pod_has_no_placement(string kind, Func<HarnessOptions, K8sJobManifest> job)
    {
        var spec = PodSpec(job(HarnessJobBuilderTests.Options));

        await Assert.That(spec.ContainsKey("tolerations")).IsFalse().Because(kind);
        await Assert.That(spec.ContainsKey("affinity")).IsFalse().Because(kind);
    }

    [Test]
    [MethodDataSource(nameof(Jobs))]
    public async Task With_helper_nodes_a_pod_tolerates_them_and_prefers_them(string kind, Func<HarnessOptions, K8sJobManifest> job)
    {
        var spec = PodSpec(job(On));

        await HarnessJobBuilderTests.AssertJsonEqual(spec["tolerations"]!.ToJsonString(),
            """[{"key":"olve.pro/role","operator":"Equal","value":"helper","effect":"NoSchedule"}]""");
        await HarnessJobBuilderTests.AssertJsonEqual(spec["affinity"]!.ToJsonString(),
            """{"nodeAffinity":{"preferredDuringSchedulingIgnoredDuringExecution":[{"weight":100,"preference":{"matchExpressions":[{"key":"olve.pro/role","operator":"In","values":["helper"]}]}}]}}""");
    }

    [Test]
    [Arguments("olve.pro/role", "Kubernetes:HelperNodes:Label 'olve.pro/role' must be key=value.")]
    [Arguments("=helper", "Kubernetes:HelperNodes:Label '=helper' must be key=value.")]
    public async Task A_malformed_label_is_refused_at_startup(string label, string message)
    {
        await Assert.That(() => HelperNodes.Parse(label, null)).Throws<InvalidOperationException>().WithMessage(message);
    }

    [Test]
    [Arguments(null, null)]
    [Arguments("", null)]
    [Arguments("olve.pro/role=helper", 100)]
    public async Task The_label_turns_the_preference_on(string? label, int? weight)
    {
        await Assert.That(HelperNodes.Parse(label, null)?.Weight).IsEqualTo(weight);
    }
}
