namespace Olve.Pipelines.Pipelines.Triggers;

/// <summary>
/// The last value each poll trigger of a pipeline observed, keyed by trigger name. Operational state
/// (not config): persisted in <c>pipeline-state.json</c> so a restart compares against the value seen
/// before it instead of re-seeding — a commit pushed just before a redeploy still triggers a run.
/// </summary>
public sealed record PollTriggerStates(IReadOnlyDictionary<string, PollTriggerState> Triggers);

/// <param name="Url">The URL the value was read from; a changed URL re-seeds instead of triggering.</param>
/// <param name="LastValue">The extracted value at <c>valuePath</c>.</param>
/// <param name="ObservedAt">When <see cref="LastValue"/> was first observed.</param>
public sealed record PollTriggerState(string Url, string LastValue, DateTimeOffset ObservedAt);
