namespace Olve.Pipelines.Kubernetes.Harness;

/// <summary>
/// The default step-pod init image (<c>harness/base</c>), shared by the server and <c>pl dev --docker</c>.
/// The tag is <c>imageBuild</c>'s content tag of <c>harness/base</c> (what <c>publish-harness</c> pushes);
/// <c>sdk/tests/runtime/harness-image.test.ts</c> fails when it no longer matches the directory.
/// </summary>
internal static class HarnessImage
{
    public const string Repository = "ghcr.io/olivervea/olve-pipelines-harness-base";
    public const string Tag = "src-db8d7779c04b";
    public const string Default = Repository + ":" + Tag;
}
