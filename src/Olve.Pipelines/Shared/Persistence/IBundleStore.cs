using Olve.Pipelines.Building;

namespace Olve.Pipelines.Shared.Persistence;

public interface IBundleStore
{
    Task UploadArtifactBundleAsync(ArtifactBundle metadata, Stream content, CancellationToken ct = default);
    Task<Stream> DownloadArtifactBundleAsync(Id<ArtifactBundle> id, CancellationToken ct = default);
    Task<IReadOnlyList<ArtifactBundle>> ListArtifactBundlesAsync(CancellationToken ct = default);
}
