namespace Olve.Pipelines.FailureHandlers;

/// <summary>
/// A pipeline's binding of a library handler to a failure scope, with the handler-config env
/// (class (b)). This is GitOps config — it lives in <c>.pipelines/config.yaml</c> and is rewritten by
/// reconcile. Any secret *value* stays out of it; only static config values belong here.
/// </summary>
/// <param name="HandlerName">A name from <see cref="FailureHandlerLibrary"/>, e.g. <c>aoe-triage</c>.</param>
/// <param name="Steps">
/// Step names this handler reacts to. Empty means whole-pipeline (any failure). Matching is by name so
/// it survives a name-preserving reconcile.
/// </param>
/// <param name="Env">Handler-config env supplied to the script (e.g. <c>AOE_BASE_URL</c>).</param>
public record FailureHandlerBinding(
    string HandlerName,
    IReadOnlyList<string> Steps,
    IReadOnlyDictionary<string, string> Env)
{
    /// <summary>True when this handler reacts to any failure in the pipeline rather than specific steps.</summary>
    public bool IsWholePipeline => Steps.Count == 0;
}

/// <summary>
/// The set of failure-handler bindings attached to a single pipeline. One attachment per pipeline (the
/// <see cref="AttachmentStore{Pipeline,T}"/> is keyed on the pipeline), holding the whole list, so
/// reconcile rewrites it wholesale and absence means "no handlers".
/// </summary>
public record FailureHandlerBindings(IReadOnlyList<FailureHandlerBinding> Bindings);
