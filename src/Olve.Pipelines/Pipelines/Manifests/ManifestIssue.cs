namespace Olve.Pipelines.Pipelines.Manifests;

public enum ManifestIssueSeverity
{
    Warning,
    Error,
}

/// <summary>
/// One manifest problem. <see cref="Code"/> is stable (for tests, GUI and docs), <see cref="Path"/> is a
/// JSON path into the manifest (e.g. <c>stages[3].stages[0].steps[0].input.artifact</c>) and
/// <see cref="Step"/> names the step involved, if any.
/// </summary>
public sealed record ManifestIssue(string Code, ManifestIssueSeverity Severity, string Message, string Path, string? Step = null)
{
    public const int WarningSeverity = 1;
    public const int ErrorSeverity = 2;

    public override string ToString() => $"[{Code}] {Path}: {Message}";

    /// <summary>
    /// As a ResultProblem: code in <c>Tags</c>, path in <c>Source</c>. User-controlled text is passed only as
    /// format arguments, never as the format string.
    /// </summary>
    public ResultProblem ToProblem() =>
        new("[{0}] {1}: {2}", Code, Path, Message)
        {
            Tags = Step is null ? [Code] : [Code, "step:" + Step],
            Source = Path,
            Severity = Severity == ManifestIssueSeverity.Error ? ErrorSeverity : WarningSeverity,
        };
}

/// <summary>Stable issue codes.</summary>
public static class ManifestIssueCodes
{
    // ── parse (structure) ──
    public const string TooLarge = "manifest-too-large";
    public const string InvalidJson = "manifest-invalid-json";
    public const string DuplicateKey = "manifest-duplicate-key";
    public const string InvalidValue = "manifest-invalid-value";
    public const string WrongType = "manifest-wrong-type";
    public const string MissingProperty = "manifest-missing-property";
    public const string UnknownProperty = "manifest-unknown-property";
    public const string UnsupportedApiVersion = "manifest-unsupported-api-version";

    // ── names ──
    public const string PipelineNameEmpty = "pipeline-name-empty";
    public const string StepNameInvalid = "step-name-invalid";
    public const string StepNameReserved = "step-name-reserved";
    public const string StepNameDuplicate = "step-name-duplicate";
    public const string ModelNameInvalid = "model-name-invalid";
    public const string SecretNameInvalid = "secret-name-invalid";

    // ── models ──
    public const string ModelUnknown = "model-unknown";
    public const string ModelUnused = "model-unused";

    // ── steps ──
    public const string StepKindEmpty = "step-kind-empty";
    public const string StepImageEmpty = "step-image-empty";
    public const string StepHashInvalid = "step-handler-hash-invalid";
    public const string StepTimeoutInvalid = "step-timeout-invalid";
    public const string StepRetriesInvalid = "step-retries-invalid";
    public const string StageEmpty = "stage-empty";
    public const string HarnessHashInvalid = "harness-hash-invalid";

    // ── inputs / mappings ──
    public const string MappingKeyMissing = "mapping-key-missing";
    public const string MappingKeyExtra = "mapping-key-extra";
    public const string RefSyntaxInvalid = "ref-syntax-invalid";
    public const string RefUnavailable = "ref-unavailable";
    public const string RefNoOutput = "ref-no-output";
    public const string RefPathUnknown = "ref-path-unknown";
    public const string RefMaybeAvailable = "ref-maybe-available";
    public const string RefFailureOutsideHandler = "ref-failure-outside-handler";
    public const string InputTypeMismatch = "input-type-mismatch";
    public const string MappingFieldMissing = "mapping-field-missing";
    public const string MappingFieldUnknown = "mapping-field-unknown";
    public const string ExternalInputClash = "external-input-clash";
    public const string ExternalInputNoSource = "external-input-no-source";
    public const string ExternalInputInFailureHandler = "external-input-in-failure-handler";
    public const string ExternalInputTitleEmpty = "external-input-title-empty";
    public const string ExternalInputSource = "external-input-source";

    // ── when ──
    public const string WhenFieldUnknown = "when-field-unknown";
    public const string WhenFieldNotLiteral = "when-field-not-literal";
    public const string WhenValueInvalid = "when-value-invalid";

    // ── secrets ──
    public const string SecretDuplicate = "secret-duplicate";
    public const string SecretUndeclared = "secret-undeclared";
    public const string SecretDuplicateInStep = "step-secret-duplicate";
    public const string PollSecretUndeclared = "poll-secret-undeclared";
    public const string PollHeaderSecretUndeclared = "poll-header-secret-undeclared";
    public const string WebhookSecretUndeclared = "webhook-secret-undeclared";

    // ── triggers ──
    public const string TriggerPushDuplicate = "trigger-push-duplicate";
    public const string TriggerNameDuplicate = "trigger-name-duplicate";
    public const string TriggerNameInvalid = "trigger-name-invalid";
    public const string TriggerIntervalInvalid = "trigger-interval-invalid";

    // ── failure handlers ──
    public const string FailureHandlerStepUnknown = "failure-handler-step-unknown";
    public const string FailureHandlerInputInvalid = "failure-handler-input-invalid";
    public const string FailureHandlerConflict = "failure-handler-conflict";

    // ── config ──
    public const string ConfigNameInvalid = "config-name-invalid";
    public const string ConfigDuplicate = "config-duplicate";
    public const string ConfigUnknown = "config-unknown";
    public const string ConfigSecretRefUndeclared = "config-secret-ref-undeclared";
    public const string ConfigSecretRefNotInStep = "config-secret-ref-not-in-step";
    public const string ConfigRefNotReceived = "config-ref-not-received";
    public const string ConfigRefFieldUnknown = "config-ref-field-unknown";
    public const string ConfigRefOutsideCase = "config-ref-outside-case";

    // ── select ──
    public const string SelectInvalid = "select-invalid";
    public const string SelectCaseMissing = "select-case-missing";
    public const string SelectCaseUnknown = "select-case-unknown";
    public const string SelectOutputMismatch = "select-output-mismatch";

    // ── warnings ──
    public const string OutputUnused = "output-unused";
}
