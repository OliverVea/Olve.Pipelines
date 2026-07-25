using Olve.Pipelines.Kubernetes;

namespace Olve.Pipelines.UnitTests;

public class KubernetesJobManifestTests
{
    private static KubernetesJobSpec Spec(string? runtimeClassName = null, string? inputPrefix = null) => new(
        Name: "olve-test",
        Image: "alpine:latest",
        Script: "echo hi",
        OutputBundleS3Prefix: "p/out",
        S3HelperImage: "minio/mc",
        S3Bucket: "olve-pipelines",
        S3Endpoint: "http://minio:9000",
        S3CredentialsSecretName: "s3-creds",
        InputBundleS3Prefix: inputPrefix,
        RuntimeClassName: runtimeClassName);

    [Test]
    public async Task BuildJobManifest_SetsRuntimeClassName_WhenConfigured()
    {
        var manifest = KubernetesClient.BuildJobManifest(Spec(runtimeClassName: "gvisor"));

        await Assert.That(manifest.Spec.Template.Spec.RuntimeClassName).IsEqualTo("gvisor");
    }

    [Test]
    public async Task BuildJobManifest_OmitsRuntimeClassName_WhenNotConfigured()
    {
        var manifest = KubernetesClient.BuildJobManifest(Spec());

        await Assert.That(manifest.Spec.Template.Spec.RuntimeClassName).IsNull();
    }

    [Test]
    public async Task BuildJobManifest_HardensPodAndAllContainers()
    {
        var manifest = KubernetesClient.BuildJobManifest(Spec(inputPrefix: "p/in"));
        var pod = manifest.Spec.Template.Spec;

        await Assert.That(pod.SecurityContext?.SeccompProfile?.Type).IsEqualTo("RuntimeDefault");

        var allContainers = pod.Containers.Concat(pod.InitContainers ?? []).ToArray();
        // s3-download + runner init containers, s3-upload main container
        await Assert.That(allContainers).Count().IsEqualTo(3);
        foreach (var container in allContainers)
        {
            await Assert.That(container.SecurityContext?.AllowPrivilegeEscalation).IsFalse();
        }
    }

    [Test]
    public async Task BuildBareJobManifest_HardensPodAndContainer()
    {
        var manifest = KubernetesClient.BuildBareJobManifest("olve-fh-x", "alpine:latest", "echo hi", null, "gvisor");
        var pod = manifest.Spec.Template.Spec;

        await Assert.That(pod.RuntimeClassName).IsEqualTo("gvisor");
        await Assert.That(pod.SecurityContext?.SeccompProfile?.Type).IsEqualTo("RuntimeDefault");
        await Assert.That(pod.Containers[0].SecurityContext?.AllowPrivilegeEscalation).IsFalse();
    }
}
