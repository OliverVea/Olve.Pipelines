using Olve.Utilities.Ids;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Processing;

public record Verification(Id<Verification> Id, string Name, Id<ProcessingStep> ProcessingStepId) : IHasId<Id<Verification>>;
