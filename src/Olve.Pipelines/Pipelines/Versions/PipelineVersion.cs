using System.Text.Json.Serialization;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Pipelines.Versions;

/// <summary>
/// An immutable snapshot of a pipeline's definition (spec §6.1), added by reconcile whenever the
/// manifest changes. <see cref="Number"/> is monotonic per pipeline.
/// </summary>
/// <param name="Manifest">The canonical manifest JSON (sorted keys), stored verbatim.</param>
/// <param name="ManifestHash">sha256 of the canonical manifest (<c>sha256:&lt;hex&gt;</c>; computed in B2).</param>
/// <param name="CommitSha">The commit the manifest was described from (null for non-git sources).</param>
/// <param name="BinaryKey">Garage key of the compiled harness (<see cref="StorageLayout.VersionBinaryKey"/>).</param>
/// <param name="StepVersions">Step name → entity version number at this pipeline version.</param>
/// <param name="ModelVersions">Model name → entity version number at this pipeline version.</param>
public record PipelineVersion(
    Id<PipelineVersion> Id,
    Id<Pipeline> PipelineId,
    int Number,
    string Manifest,
    string ManifestHash,
    string? CommitSha,
    string BinaryKey,
    IReadOnlyDictionary<string, int> StepVersions,
    IReadOnlyDictionary<string, int> ModelVersions,
    DateTimeOffset CreatedAt) : IHasId<Id<PipelineVersion>>;

/// <summary>
/// Which version of a pipeline starts new runs (spec §6.2) — operational state attached to the
/// pipeline, not config. Absent = no version yet (nothing can run).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Active), "active")]
[JsonDerivedType(typeof(Draining), "draining")]
public abstract record PipelineActivation
{
    /// <summary>New runs start on this version.</summary>
    public sealed record Active(
        Id<PipelineVersion> VersionId,
        int VersionNumber,
        DateTimeOffset Since) : PipelineActivation;

    /// <summary>
    /// A switch in progress: <c>From</c> starts no new runs while its in-flight jobs finish
    /// (or are cancelled when <see cref="CancelRunning"/>); triggers queue until it completes to
    /// <see cref="Active"/>(<c>To</c>).
    /// </summary>
    public sealed record Draining(
        Id<PipelineVersion> FromVersionId,
        int FromVersionNumber,
        Id<PipelineVersion> ToVersionId,
        int ToVersionNumber,
        bool CancelRunning,
        DateTimeOffset Since) : PipelineActivation;
}
