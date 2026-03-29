using Olve.Utilities.Ids;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Pipelines;

public record Pipeline(Id<Pipeline> Id, string Name) : IHasId<Id<Pipeline>>;
