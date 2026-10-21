using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Pipelines;

/// <param name="Description">From the active version's manifest (null until the first reconcile).</param>
public record Pipeline(Id<Pipeline> Id, string Name, string? Description = null) : IHasId<Id<Pipeline>>;
