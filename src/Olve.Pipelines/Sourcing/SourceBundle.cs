using Olve.Pipelines.Pipelines;
using Olve.Utilities.Ids;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Sourcing;

public enum SourceBundleStatus { Pending, Completed, Failed }

public record SourceBundle(
    Id<SourceBundle> Id,
    Id<Pipeline> PipelineId,
    DateTimeOffset CreatedAt,
    SourceBundleStatus Status) : IHasId<Id<SourceBundle>>;
