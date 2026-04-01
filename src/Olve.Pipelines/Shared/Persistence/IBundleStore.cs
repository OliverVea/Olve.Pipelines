using Olve.Pipelines.Building;
using Olve.Pipelines.Sourcing;

namespace Olve.Pipelines.Shared.Persistence;

public interface IBundleStore
{
    Task UploadSourceBundleAsync(SourceBundle metadata, Stream content, CancellationToken ct = default);
    Task UploadArtifactBundleAsync(ArtifactBundle metadata, Stream content, CancellationToken ct = default);
    Task<Stream> DownloadSourceBundleAsync(Id<SourceBundle> id, CancellationToken ct = default);
    Task<Stream> DownloadArtifactBundleAsync(Id<ArtifactBundle> id, CancellationToken ct = default);
    Task<IReadOnlyList<SourceBundle>> ListSourceBundlesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ArtifactBundle>> ListArtifactBundlesAsync(CancellationToken ct = default);
}
