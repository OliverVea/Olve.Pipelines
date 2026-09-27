using Olve.Pipelines.Kubernetes;

namespace Olve.Pipelines.UnitTests;

public class KubernetesJobManifestTests
{
    private static KubernetesJobSpec Spec(string? runtimeClassName = null, string? inputPrefix = null) => new(
        Name: "olve-test",
        Image: "alpine:latest",
        Script: "echo hi",
        OutputBundleS3Prefix: "p/out",
        S3HelperImage: "curlimages/curl",
        S3Bucket: "olve-pipelines",
        S3Endpoint: "http://garage:3900",
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
    public async Task BuildJobManifest_S3Helpers_RunSyncScriptWithPrefixAndCredentials()
    {
        var manifest = KubernetesClient.BuildJobManifest(Spec(inputPrefix: "p/in"));
        var pod = manifest.Spec.Template.Spec;

        var download = pod.InitContainers!.Single(c => c.Name == "s3-download");
        var upload = pod.Containers.Single(c => c.Name == "s3-upload");

        await Assert.That(download.Args).IsEquivalentTo(new[] { S3SyncScript.Content, "s3sync", "download" });
        await Assert.That(upload.Args).IsEquivalentTo(new[] { S3SyncScript.Content, "s3sync", "upload" });
        await Assert.That(download.Env!.Single(e => e.Name == "S3_PREFIX").Value).IsEqualTo("p/in");
        await Assert.That(upload.Env!.Single(e => e.Name == "S3_PREFIX").Value).IsEqualTo("p/out");
        await Assert.That(upload.Env!.Single(e => e.Name == "S3_ENDPOINT").Value).IsEqualTo("http://garage:3900");
        await Assert.That(upload.EnvFrom![0].SecretRef.Name).IsEqualTo("s3-creds");
    }

    [Test]
    public async Task StepJobs_ExpireAfterAWeek_FailureHandlerJobsDoNot()
    {
        var step = KubernetesClient.BuildJobManifest(Spec());
        var handler = KubernetesClient.BuildBareJobManifest("olve-fh-x", "alpine:latest", "echo hi", null, null);

        await Assert.That(step.Spec.TtlSecondsAfterFinished).IsEqualTo(7 * 24 * 60 * 60);
        // Nothing persists failure-handler logs, so their pods must not be auto-deleted.
        await Assert.That(handler.Spec.TtlSecondsAfterFinished).IsNull();
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
