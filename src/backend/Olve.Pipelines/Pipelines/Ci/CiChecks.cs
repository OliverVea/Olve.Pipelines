using Olve.Pipelines.Pipelines.Manifests;

namespace Olve.Pipelines.Pipelines.Ci;

/// <summary>
/// Whether two definitions check a pull request the same way: the same <c>ci</c> section and the same code hash. Step
/// code has no hash of its own, so any change to the pipeline's code counts.
/// </summary>
public static class CiChecks
{
    public static bool Same(Manifest a, Manifest b) =>
        a.CodeHash == b.CodeHash && Canonical(a) == Canonical(b);

    private static string? Canonical(Manifest m) =>
        m.Ci is { } ci ? CanonicalJson.Serialize(ManifestWriter.ToElement(w => ManifestWriter.Write(w, ci))) : null;
}
