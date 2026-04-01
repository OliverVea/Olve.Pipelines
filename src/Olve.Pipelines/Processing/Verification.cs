using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Processing;

public enum VerificationType { None, Script }

public record Verification(Id<Verification> Id, string Name, Id<ProcessingStep> ProcessingStepId, VerificationType Type = VerificationType.None) : IHasId<Id<Verification>>;
