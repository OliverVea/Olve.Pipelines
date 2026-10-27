using System.Text.Json;
using System.Text.RegularExpressions;

namespace Olve.Pipelines.Pipelines.Manifests;

/// <summary>Validation outcome: errors block a reconcile; warnings are informational.</summary>
public sealed record ManifestValidationReport(IReadOnlyList<ManifestIssue> Errors, IReadOnlyList<ManifestIssue> Warnings)
{
    public bool IsValid => Errors.Count == 0;

    /// <summary>Success carries the warnings; failure carries one ResultProblem per error.</summary>
    public Result<IReadOnlyList<ManifestIssue>> ToResult() =>
        IsValid
            ? Result.Success(Warnings)
            : Result.Failure<IReadOnlyList<ManifestIssue>>(Errors.Select(e => e.ToProblem()));
}

/// <summary>
/// Reconcile-time manifest checks (design §3.7, "Reconcile" column). Re-implements every manifest-level
/// check independently of the SDK — the service never trusts that the compiler or describe ran — and
/// reports all problems at once. Pure and synchronous.
/// </summary>
public static partial class ManifestValidator
{
    public static readonly IReadOnlySet<string> ReservedStepNames =
        new HashSet<string>(StringComparer.Ordinal) { ManifestRoots.Input, ManifestRoots.Failure };

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex StepNameRegex();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex ModelNameRegex();

    [GeneratedRegex("^[A-Z_][A-Z0-9_]*$")]
    private static partial Regex SecretNameRegex();

    [GeneratedRegex("^[a-z][a-zA-Z0-9]*$")]
    private static partial Regex ConfigNameRegex();

    [GeneratedRegex("^sha256:[0-9a-f]{64}$")]
    private static partial Regex HashRegex();

    [GeneratedRegex(@"^(?:input|failure|[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)(?:\.[^.]+)*$")]
    private static partial Regex RefRegex();

    [GeneratedRegex(@"\$SECRET:([A-Za-z0-9_]+)")]
    private static partial Regex SecretReferenceRegex();

    public static bool IsValidStepName(string name) => StepNameRegex().IsMatch(name);

    public static ManifestValidationReport Validate(Manifest manifest) => new Checker(manifest).Run();

    private readonly record struct Avail(string? Model, bool Maybe);

    /// <summary>A select case a step sits in: the select (by reference), the value, and its condition text.</summary>
    private sealed record CaseOf(SelectStage Select, string Value)
    {
        public string Condition => Select.Condition(Value);
    }

    private sealed class Checker(Manifest m)
    {
        private readonly List<ManifestIssue> _errors = [];
        private readonly List<ManifestIssue> _warnings = [];
        private readonly HashSet<string> _secrets = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ManifestConfigEntry> _config = new(StringComparer.Ordinal);
        private readonly HashSet<string> _usedModels = new(StringComparer.Ordinal);
        private readonly HashSet<string> _consumedRoots = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _stageSteps = new(StringComparer.Ordinal); // name → first path
        private readonly Dictionary<string, List<(string Path, IReadOnlyList<CaseOf> Cases, string? Output)>> _placements = new(StringComparer.Ordinal);
        private readonly List<(string Name, string Path)> _producers = [];

        private void Error(string code, string path, string message, string? step = null) =>
            _errors.Add(new ManifestIssue(code, ManifestIssueSeverity.Error, message, path, step));

        private void Warn(string code, string path, string message, string? step = null) =>
            _warnings.Add(new ManifestIssue(code, ManifestIssueSeverity.Warning, message, path, step));

        public ManifestValidationReport Run()
        {
            CheckHeader();
            CheckModels();
            CheckSecrets();
            CheckConfig();
            CheckTriggers();

            var avail = new Dictionary<string, Avail>(StringComparer.Ordinal);
            if (UseModel(m.Input, "input", "pipeline input")) avail[ManifestRoots.Input] = new Avail(m.Input, false);
            else avail[ManifestRoots.Input] = new Avail(null, false);
            WalkStages(m.Stages, "stages", avail);

            CheckFailureHandlers();
            CheckUnused();
            return new ManifestValidationReport(_errors, _warnings);
        }

        // ── header ──
        private void CheckHeader()
        {
            if (string.IsNullOrWhiteSpace(m.Name)) Error(ManifestIssueCodes.PipelineNameEmpty, "name", "the pipeline name must not be empty");
            if (!HashRegex().IsMatch(m.Harness.SdkHash))
                Error(ManifestIssueCodes.HarnessHashInvalid, "harness.sdkHash", $"'{m.Harness.SdkHash}' is not a sha256:<64 hex> hash");
            if (m.Retention is { } r)
            {
                if (r.KeepLatestRuns is < 0) Error(ManifestIssueCodes.RetentionInvalid, "retention.keepLatestRuns", "retention.keepLatestRuns must be a non-negative integer");
                if (r.MinRunAgeDays is < 0) Error(ManifestIssueCodes.RetentionInvalid, "retention.minRunAgeDays", "retention.minRunAgeDays must be a non-negative integer");
                if (r.BuildGraceDays is < 0) Error(ManifestIssueCodes.RetentionInvalid, "retention.buildGraceDays", "retention.buildGraceDays must be a non-negative integer");
            }
        }

        // ── models ──
        private void CheckModels()
        {
            foreach (var name in m.Models.Keys)
                if (!ModelNameRegex().IsMatch(name))
                    Error(ManifestIssueCodes.ModelNameInvalid, $"models.{name}", $"model name '{name}' must match {ModelNameRegex()}");
        }

        /// <summary>Records a model reference; reports it if the model is not in <c>models</c>.</summary>
        private bool UseModel(string name, string path, string what, string? step = null)
        {
            _usedModels.Add(name);
            if (m.Models.ContainsKey(name)) return true;
            Error(ManifestIssueCodes.ModelUnknown, path, $"{what} references model '{name}', which is not in models", step);
            return false;
        }

        // ── secrets ──
        private void CheckSecrets()
        {
            for (var i = 0; i < m.Secrets.Count; i++)
            {
                var s = m.Secrets[i];
                var path = $"secrets[{i}].name";
                if (!SecretNameRegex().IsMatch(s.Name)) Error(ManifestIssueCodes.SecretNameInvalid, path, $"secret name '{s.Name}' must match {SecretNameRegex()}");
                if (!_secrets.Add(s.Name)) Error(ManifestIssueCodes.SecretDuplicate, path, $"secret '{s.Name}' is declared more than once");
            }
        }

        // ── triggers ──
        private void CheckTriggers()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var pushes = 0;
            for (var i = 0; i < m.Triggers.Count; i++)
            {
                var t = m.Triggers[i];
                var path = $"triggers[{i}]";
                if (t is PushTrigger)
                {
                    if (++pushes > 1)
                    {
                        Error(ManifestIssueCodes.TriggerPushDuplicate, path, "there is more than one push trigger (onPush may be used once)");
                        continue;
                    }
                }
                if (!names.Add(t.Name)) Error(ManifestIssueCodes.TriggerNameDuplicate, $"{path}.name", $"duplicate trigger name '{t.Name}'");
                if (t is WebhookTrigger h)
                {
                    if (!StepNameRegex().IsMatch(h.Name))
                        Error(ManifestIssueCodes.TriggerNameInvalid, $"{path}.name", $"webhook trigger name '{h.Name}' must be lower-case kebab (1-63 chars)");
                    if (!_secrets.Contains(h.Verify.Secret))
                        Error(ManifestIssueCodes.WebhookSecretUndeclared, $"{path}.verify.secret",
                            $"webhook '{h.Name}' verifies with secret '{h.Verify.Secret}', which is not in the pipeline's secrets");
                    if (h.Verify is HmacSha256TriggerVerify { Header.Length: 0 })
                        Error(ManifestIssueCodes.InvalidValue, $"{path}.verify.header", $"webhook '{h.Name}': the signature header must not be empty");
                    continue;
                }
                if (t is not PollTrigger p) continue;
                if (!StepNameRegex().IsMatch(p.Name))
                    Error(ManifestIssueCodes.TriggerNameInvalid, $"{path}.name", $"poll trigger name '{p.Name}' must be lower-case kebab (1-63 chars)");
                if (p.IntervalSeconds <= 0)
                    Error(ManifestIssueCodes.TriggerIntervalInvalid, $"{path}.intervalSeconds", $"poll '{p.Name}': intervalSeconds must be a positive integer");
                var declared = new HashSet<string>(StringComparer.Ordinal);
                for (var j = 0; j < p.Secrets.Count; j++)
                {
                    var s = p.Secrets[j];
                    declared.Add(s);
                    if (!SecretNameRegex().IsMatch(s))
                        Error(ManifestIssueCodes.SecretNameInvalid, $"{path}.secrets[{j}]", $"secret name '{s}' must match {SecretNameRegex()}");
                    if (!_secrets.Contains(s))
                        Error(ManifestIssueCodes.PollSecretUndeclared, $"{path}.secrets[{j}]", $"poll '{p.Name}' uses secret '{s}', which is not in the pipeline's secrets");
                }
                foreach (var (header, value) in p.Headers)
                    foreach (Match match in SecretReferenceRegex().Matches(value))
                    {
                        var secret = match.Groups[1].Value;
                        if (!declared.Contains(secret))
                            Error(ManifestIssueCodes.PollHeaderSecretUndeclared, $"{path}.headers.{header}",
                                $"poll '{p.Name}': header '{header}' references secret '{secret}', which is not in the poll's secrets");
                    }
            }
        }

        // ── stages ──
        private void WalkStages(IReadOnlyList<ManifestStage> stages, string path, Dictionary<string, Avail> avail, IReadOnlyList<CaseOf>? cases = null)
        {
            cases ??= [];
            for (var i = 0; i < stages.Count; i++)
            {
                var stagePath = $"{path}[{i}]";
                switch (stages[i])
                {
                    case StepsStage ss:
                        if (ss.Steps.Count == 0) Error(ManifestIssueCodes.StageEmpty, $"{stagePath}.steps", "a stage needs at least one step");
                        for (var j = 0; j < ss.Steps.Count; j++)
                        {
                            var step = ss.Steps[j];
                            var stepPath = $"{stagePath}.steps[{j}]";
                            RegisterStageStep(step, stepPath, cases);
                            CheckStep(step, stepPath, avail, failureHandler: false, cases);
                        }
                        // Outputs become available only after the barrier: same-stage siblings can't see each other.
                        for (var j = 0; j < ss.Steps.Count; j++)
                        {
                            var step = ss.Steps[j];
                            avail[step.Name] = new Avail(step.Output, false);
                            if (step.Output is not null) _producers.Add((step.Name, $"{stagePath}.steps[{j}]"));
                        }
                        break;
                    case WhenStage ws:
                        CheckWhen(ws, $"{stagePath}.when");
                        var inner = new Dictionary<string, Avail>(avail, StringComparer.Ordinal);
                        WalkStages(ws.Stages, $"{stagePath}.stages", inner, cases);
                        foreach (var (name, a) in inner)
                            if (!avail.ContainsKey(name))
                                avail[name] = a with { Maybe = true };
                        break;
                    case SelectStage sel:
                        CheckSelect(sel, stagePath, avail, cases);
                        break;
                }
            }
        }

        private void RegisterStageStep(StepDescriptor step, string path, IReadOnlyList<CaseOf> cases)
        {
            CheckStepName(step.Name, path);
            _stageSteps.TryAdd(step.Name, path);
            if (!_placements.TryGetValue(step.Name, out var placed)) _placements[step.Name] = placed = [];
            // Only steps in different cases of one select may share a name: exactly one of them exists in a run.
            foreach (var other in placed)
            {
                if (!Exclusive(other.Cases, cases))
                    Error(ManifestIssueCodes.StepNameDuplicate, $"{path}.name", $"duplicate step name '{step.Name}' (also at {other.Path})", step.Name);
                else if (!string.Equals(other.Output, step.Output, StringComparison.Ordinal))
                    Error(ManifestIssueCodes.SelectOutputMismatch, $"{path}.output",
                        $"step '{step.Name}' has output {step.Output ?? "none"} here and {other.Output ?? "none"} at {other.Path}; select cases must agree", step.Name);
            }
            placed.Add((path, cases, step.Output));
        }

        private static bool Exclusive(IReadOnlyList<CaseOf> a, IReadOnlyList<CaseOf> b) =>
            a.Any(x => b.Any(y => ReferenceEquals(x.Select, y.Select) && x.Value != y.Value));

        // ── select ──
        private void CheckSelect(SelectStage sel, string path, Dictionary<string, Avail> avail, IReadOnlyList<CaseOf> cases)
        {
            var segments = sel.Select.Split('.');
            if (!_config.TryGetValue(segments[0], out var entry))
                Error(ManifestIssueCodes.SelectInvalid, $"{path}.select", $"select '{sel.Select}': '{segments[0]}' is not in the pipeline's config");
            else if (DiscriminatorValues(entry.Schema, segments[1..], sel.Discriminator) is not { } values)
                Error(ManifestIssueCodes.SelectInvalid, $"{path}.select",
                    $"select '{sel.Select}': it must reference a union whose branches each fix '{sel.Discriminator}' to a string");
            else
            {
                foreach (var v in values.Where(v => !sel.Cases.ContainsKey(v)))
                    Error(ManifestIssueCodes.SelectCaseMissing, $"{path}.cases", $"select '{sel.Select}': no case for {sel.Discriminator} '{v}'");
                foreach (var v in sel.Cases.Keys.Where(v => !values.Contains(v)))
                    Error(ManifestIssueCodes.SelectCaseUnknown, $"{path}.cases.{v}", $"select '{sel.Select}': '{v}' is not a value of {sel.Discriminator} ({string.Join(", ", values)})");
            }

            var branches = new List<Dictionary<string, Avail>>();
            foreach (var (value, stages) in sel.Cases)
            {
                var inner = new Dictionary<string, Avail>(avail, StringComparer.Ordinal);
                WalkStages(stages, $"{path}.cases.{value}", inner, [.. cases, new CaseOf(sel, value)]);
                branches.Add(inner);
            }
            // A step every case has is as available as in its cases (one case always runs); one only some have is maybe-available.
            foreach (var name in branches.SelectMany(b => b.Keys).Where(k => !avail.ContainsKey(k)).Distinct(StringComparer.Ordinal).ToList())
            {
                var present = branches.Where(b => b.ContainsKey(name)).Select(b => b[name]).ToList();
                avail[name] = new Avail(present[0].Model, present.Count < branches.Count || present.Any(a => a.Maybe));
            }
        }

        /// <summary>The string values <paramref name="discriminator"/> takes across the union at <paramref name="path"/>, or null.</summary>
        private static IReadOnlyList<string>? DiscriminatorValues(JsonElement schema, IReadOnlyList<string> path, string discriminator)
        {
            if (!JsonSchemaShapes.TryResolveField(schema, schema, path, out var leaf) || leaf is not { } union) return null;
            if (!union.TryGetProperty("oneOf", out var branches) && !union.TryGetProperty("anyOf", out branches)) return null;
            if (branches.ValueKind != JsonValueKind.Array) return null;
            var values = new List<string>();
            foreach (var branch in branches.EnumerateArray())
            {
                if (!JsonSchemaShapes.TryResolveField(schema, branch, [discriminator], out var field) || field is not { } f) return null;
                var literals = JsonSchemaShapes.LiteralValues(schema, f);
                if (literals is null || literals.Any(l => l.ValueKind != JsonValueKind.String)) return null;
                values.AddRange(literals.Select(l => l.GetString()!));
            }
            return values;
        }

        private void CheckStepName(string name, string path)
        {
            if (!StepNameRegex().IsMatch(name))
                Error(ManifestIssueCodes.StepNameInvalid, $"{path}.name", $"step name '{name}' must be lower-case kebab, 1-63 chars ({StepNameRegex()})", name);
            if (ReservedStepNames.Contains(name))
                Error(ManifestIssueCodes.StepNameReserved, $"{path}.name", $"'{name}' is reserved and cannot be a step name", name);
        }

        // ── when ──
        private void CheckWhen(WhenStage ws, string path)
        {
            if (!m.Models.TryGetValue(m.Input, out var inputModel)) return; // reported as model-unknown
            var root = inputModel.Output;
            var shape = JsonSchemaShapes.ObjectShapeOf(root, root);
            foreach (var (field, values) in ws.When)
            {
                var fieldPath = $"{path}.{field}";
                if (shape is null || !shape.Properties.TryGetValue(field, out var fieldSchema))
                {
                    Error(ManifestIssueCodes.WhenFieldUnknown, fieldPath, $"'{field}' is not a field of the pipeline input {m.Input}");
                    continue;
                }
                var literals = JsonSchemaShapes.LiteralValues(root, fieldSchema);
                if (literals is null)
                {
                    Error(ManifestIssueCodes.WhenFieldNotLiteral, fieldPath, $"'{field}' is not a literal-like field (enum, boolean or literal)");
                    continue;
                }
                if (values.Count == 0) Error(ManifestIssueCodes.WhenValueInvalid, fieldPath, $"'{field}' has no values");
                foreach (var v in values)
                    if (!literals.Any(l => JsonElement.DeepEquals(l, v)))
                        Error(ManifestIssueCodes.WhenValueInvalid, fieldPath,
                            $"{v.GetRawText()} is not a valid value of '{field}' ({string.Join(", ", literals.Select(l => l.GetRawText()))})");
            }
        }

        // ── one step ──
        private void CheckStep(StepDescriptor s, string path, IReadOnlyDictionary<string, Avail> avail, bool failureHandler, IReadOnlyList<CaseOf>? cases = null)
        {
            var n = s.Name;
            if (string.IsNullOrWhiteSpace(s.Kind)) Error(ManifestIssueCodes.StepKindEmpty, $"{path}.kind", $"step '{n}': kind must not be empty", n);
            if (string.IsNullOrWhiteSpace(s.Image)) Error(ManifestIssueCodes.StepImageEmpty, $"{path}.image", $"step '{n}': image must not be empty", n);
            if (!HashRegex().IsMatch(s.HandlerHash))
                Error(ManifestIssueCodes.StepHashInvalid, $"{path}.handlerHash", $"step '{n}': handlerHash '{s.HandlerHash}' is not a sha256:<64 hex> hash", n);
            if (s.TimeoutSeconds is <= 0) Error(ManifestIssueCodes.StepTimeoutInvalid, $"{path}.timeoutSeconds", $"step '{n}': timeoutSeconds must be a positive integer", n);
            if (s.Retries is < 0) Error(ManifestIssueCodes.StepRetriesInvalid, $"{path}.retries", $"step '{n}': retries must be a non-negative integer", n);

            var stepSecrets = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < s.Secrets.Count; i++)
            {
                var secret = s.Secrets[i];
                var sp = $"{path}.secrets[{i}]";
                if (!SecretNameRegex().IsMatch(secret)) Error(ManifestIssueCodes.SecretNameInvalid, sp, $"secret name '{secret}' must match {SecretNameRegex()}", n);
                if (!stepSecrets.Add(secret)) Error(ManifestIssueCodes.SecretDuplicateInStep, sp, $"step '{n}' lists secret '{secret}' twice", n);
                if (!_secrets.Contains(secret)) Error(ManifestIssueCodes.SecretUndeclared, sp, $"step '{n}' uses secret '{secret}', which is not in the pipeline's secrets", n);
            }

            CheckStepConfig(s, path, stepSecrets);
            CheckConfigRefs(s, path, cases ?? []);

            if (s.Output is not null) UseModel(s.Output, $"{path}.output", $"step '{n}' output", n);

            foreach (var key in s.InputTypes.Keys.Where(k => !s.Input.ContainsKey(k)))
                Error(ManifestIssueCodes.MappingKeyMissing, $"{path}.input", $"step '{n}': input '{key}' has no mapping", n);
            foreach (var key in s.Input.Keys.Where(k => !s.InputTypes.ContainsKey(k)))
                Error(ManifestIssueCodes.MappingKeyExtra, $"{path}.input.{key}", $"step '{n}': mapping '{key}' is not an input of the step", n);

            foreach (var (key, external) in s.ExternalInputs)
            {
                var xp = $"{path}.externalInputs.{key}";
                var at = $"step '{n}' external input '{key}'";
                UseModel(external.Model, $"{xp}.model", at, n);
                if (s.InputTypes.ContainsKey(key) || s.Input.ContainsKey(key))
                    Error(ManifestIssueCodes.ExternalInputClash, xp, $"{at}: '{key}' is also a regular input", n);
                if (key.StartsWith('$'))
                    Error(ManifestIssueCodes.ExternalInputClash, xp, $"{at}: keys starting with '$' are reserved", n);
                if (string.IsNullOrWhiteSpace(external.Title))
                    Error(ManifestIssueCodes.ExternalInputTitleEmpty, $"{xp}.title", $"{at}: title must not be empty", n);
                if (external.From.Count == 0)
                    Error(ManifestIssueCodes.ExternalInputNoSource, $"{xp}.from", $"{at}: from must list at least one source", n);
                if (failureHandler)
                    Error(ManifestIssueCodes.ExternalInputInFailureHandler, xp, $"{at}: failure handlers run unattended and cannot wait for input", n);
                if (external.From.GroupBy(f => f.Kind).Any(g => g.Count() > 1))
                    Error(ManifestIssueCodes.ExternalInputSource, $"{xp}.from", $"{at}: each source kind may be listed once", n);
                if (external.From.Any(f => f is ExternalSource.Callback) && !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
                    Error(ManifestIssueCodes.ExternalInputSource, xp, $"{at}: a callback input's key must be letters, digits, '_' or '-' (it is part of the URL)", n);
                foreach (var poll in external.From.OfType<ExternalSource.Poll>())
                {
                    var url = poll.Url.Replace("{run.id}", "x", StringComparison.Ordinal).Replace("{run.number}", "1", StringComparison.Ordinal);
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                        Error(ManifestIssueCodes.ExternalInputSource, $"{xp}.from", $"{at}: poll url must be an absolute http(s) URL", n);
                    if (string.IsNullOrWhiteSpace(poll.ValuePath))
                        Error(ManifestIssueCodes.ExternalInputSource, $"{xp}.from", $"{at}: poll valuePath must not be empty", n);
                    if (poll.IntervalSeconds < 10)
                        Error(ManifestIssueCodes.ExternalInputSource, $"{xp}.from", $"{at}: poll intervalSeconds must be at least 10", n);
                }
            }

            foreach (var (key, type) in s.InputTypes)
            {
                var tp = $"{path}.inputTypes.{key}";
                if (type is ModelInputType mt) UseModel(mt.Model, $"{tp}.model", $"step '{n}' input '{key}'", n);
                if (!s.Input.TryGetValue(key, out var mapping)) continue;
                var mp = $"{path}.input.{key}";
                var at = $"step '{n}' input '{key}'";
                if (failureHandler)
                {
                    var ok = type is ModelInputType { Model: ManifestRoots.FailureContextModel }
                             && mapping is RefMapping { Ref: ManifestRoots.Failure };
                    if (!ok)
                        Error(ManifestIssueCodes.FailureHandlerInputInvalid, mp,
                            $"{at}: a failure handler's inputs must all be {ManifestRoots.FailureContextModel} mapped from {{\"ref\": \"failure\"}}", n);
                    continue;
                }
                switch (type)
                {
                    case ModelInputType model:
                        CheckModelInput(at, mp, n, model, mapping, avail);
                        break;
                    case SchemaInputType schema:
                        var target = schema.Schema.Input ?? schema.Schema.Output;
                        CheckMapping(at, mp, n, mapping, target, target, avail);
                        break;
                }
            }
        }

        /// <summary>
        /// A model input. A whole-output ref (<c>{"ref": "build"}</c>, what model matching emits) must come from a
        /// producer of exactly that model — models are nominal. A field ref, constant or field-by-field object
        /// is checked structurally against the model's schema (the SDK's brand is optional on values, so
        /// rebuilding a model from refs type-checks; see sdk/tests/compile-pass/mapper-shapes.ts).
        /// </summary>
        private void CheckModelInput(string at, string path, string step, ModelInputType type, Mapping mapping, IReadOnlyDictionary<string, Avail> avail)
        {
            if (mapping is RefMapping { Path.Count: 0 } r)
            {
                if (!CheckRefSyntax(at, path, step, r)) return;
                if (!TryRoot(at, path, step, r, avail, type.Optional, out var a)) return;
                if (a.Model != type.Model)
                    Error(ManifestIssueCodes.InputTypeMismatch, path, $"{at}: expects {type.Model} but '{r.Root}' produces {a.Model} (models are nominal)", step);
                if (a.Maybe && !type.Optional)
                    Error(ManifestIssueCodes.RefMaybeAvailable, path,
                        $"{at}: {a.Model} from '{r.Root}' is only maybe-available (produced inside a when block); declare it optional({type.Model})", step);
                return;
            }
            if (!m.Models.TryGetValue(type.Model, out var model))
            {
                CheckMapping(at, path, step, mapping, default, null, avail, type.Optional);
                return;
            }
            var target = model.Input ?? model.Output;
            CheckMapping(at, path, step, mapping, target, target, avail, type.Optional);
        }

        private bool CheckRefSyntax(string at, string path, string step, RefMapping r)
        {
            if (RefRegex().IsMatch(r.Ref)) return true;
            Error(ManifestIssueCodes.RefSyntaxInvalid, path, $"{at}: '{r.Ref}' is not a valid ref ('<root>' or '<root>.<field>…')", step);
            return false;
        }

        /// <summary>Resolves a ref's root against what is available at this stage.</summary>
        private bool TryRoot(string at, string path, string step, RefMapping r, IReadOnlyDictionary<string, Avail> avail, bool optionalTarget, out Avail a)
        {
            _consumedRoots.Add(r.Root);
            if (r.Root == ManifestRoots.Failure)
            {
                Error(ManifestIssueCodes.RefFailureOutsideHandler, path, $"{at}: '{ManifestRoots.Failure}' is only available to failure handlers", step);
                a = default;
                return false;
            }
            if (!avail.TryGetValue(r.Root, out a))
            {
                var hint = optionalTarget ? " (optional() means maybe-skipped; there must still be an earlier producer)" : "";
                var where = _stageSteps.ContainsKey(r.Root) ? " (it runs in the same or a later stage)" : "";
                Error(ManifestIssueCodes.RefUnavailable, path, $"{at}: '{r.Root}' is not available at this stage{where}{hint}", step);
                return false;
            }
            if (a.Model is null)
            {
                Error(ManifestIssueCodes.RefNoOutput, path, $"{at}: step '{r.Root}' produces no output", step);
                return false;
            }
            return true;
        }

        /// <summary>
        /// A mapping checked structurally: refs must resolve, types must not be disjoint and required object
        /// fields must be provided (lenient where the schema doesn't say). <paramref name="allowMaybe"/> is true
        /// only for the top-level mapping of an optional input (a skipped root then yields no value).
        /// </summary>
        private void CheckMapping(string at, string path, string step, Mapping mapping, JsonElement targetRoot, JsonElement? target,
            IReadOnlyDictionary<string, Avail> avail, bool allowMaybe = false)
        {
            switch (mapping)
            {
                case RefMapping r:
                    {
                        if (!CheckRefSyntax(at, path, step, r) || !TryRoot(at, path, step, r, avail, allowMaybe, out var a)) return;
                        if (a.Maybe && !allowMaybe)
                            Error(ManifestIssueCodes.RefMaybeAvailable, path,
                                $"{at}: '{r.Ref}' is only maybe-available (produced inside a when block); map it into an optional(...) input", step);
                        if (!m.Models.TryGetValue(a.Model!, out var source)) return;
                        if (!JsonSchemaShapes.TryResolveField(source.Output, source.Output, r.Path, out var leaf))
                        {
                            Error(ManifestIssueCodes.RefPathUnknown, path, $"{at}: '{r.Ref}': model {a.Model} has no field '{string.Join('.', r.Path)}'", step);
                            return;
                        }
                        if (target is { } t && leaf is { } l) CheckShape(at, path, step, r.Ref, source.Output, l, targetRoot, t, 0);
                        return;
                    }
                case ConstMapping c:
                    {
                        if (target is not { } t) return;
                        var want = JsonSchemaShapes.TypesOf(targetRoot, t);
                        var got = JsonSchemaShapes.KindOf(c.Value);
                        if (!JsonSchemaShapes.Overlaps(want, [got]))
                            Error(ManifestIssueCodes.InputTypeMismatch, path, $"{at}: constant is {got} but the input expects {string.Join("|", want!.Order())}", step);
                        return;
                    }
                case ObjectMapping o:
                    {
                        JsonSchemaShapes.ObjectShape? shape = null;
                        if (target is { } t)
                        {
                            var want = JsonSchemaShapes.TypesOf(targetRoot, t);
                            if (!JsonSchemaShapes.Overlaps(want, ["object"]))
                            {
                                Error(ManifestIssueCodes.InputTypeMismatch, path, $"{at}: an object is mapped but the input expects {string.Join("|", want!.Order())}", step);
                                return;
                            }
                            shape = JsonSchemaShapes.ObjectShapeOf(targetRoot, t);
                        }
                        foreach (var (field, inner) in o.Fields)
                        {
                            JsonElement? fieldSchema = null;
                            if (shape is not null)
                            {
                                if (shape.Properties.TryGetValue(field, out var fs)) fieldSchema = fs;
                                else if (shape.Closed)
                                    Error(ManifestIssueCodes.MappingFieldUnknown, $"{path}.{field}", $"{at}: field '{field}' is not part of the input's schema", step);
                            }
                            CheckMapping($"{at}.{field}", $"{path}.{field}", step, inner, targetRoot, fieldSchema, avail);
                        }
                        if (shape is not null)
                            foreach (var req in shape.Required.Where(req => !o.Fields.ContainsKey(req)).Order(StringComparer.Ordinal))
                                Error(ManifestIssueCodes.MappingFieldMissing, path, $"{at}: required field '{req}' is not mapped", step);
                        return;
                    }
            }
        }

        private const int MaxShapeDepth = 16;

        /// <summary>A referenced field's schema against the target schema: overlapping types, required fields present.</summary>
        private void CheckShape(string at, string path, string step, string reference, JsonElement sourceRoot, JsonElement source,
            JsonElement targetRoot, JsonElement target, int depth)
        {
            var want = JsonSchemaShapes.TypesOf(targetRoot, target);
            var got = JsonSchemaShapes.TypesOf(sourceRoot, source);
            if (!JsonSchemaShapes.Overlaps(want, got))
            {
                Error(ManifestIssueCodes.InputTypeMismatch, path,
                    $"{at}: '{reference}' is {string.Join("|", got!.Order())} but the input expects {string.Join("|", want!.Order())}", step);
                return;
            }
            if (depth >= MaxShapeDepth) return;
            var targetShape = JsonSchemaShapes.ObjectShapeOf(targetRoot, target);
            var sourceShape = JsonSchemaShapes.ObjectShapeOf(sourceRoot, source);
            if (targetShape is null || sourceShape is null) return;
            foreach (var req in targetShape.Required.Order(StringComparer.Ordinal))
            {
                if (!sourceShape.Properties.ContainsKey(req))
                    Error(ManifestIssueCodes.MappingFieldMissing, path, $"{at}: '{reference}' has no field '{req}', which the input requires", step);
                else if (!sourceShape.Required.Contains(req))
                    Error(ManifestIssueCodes.MappingFieldMissing, path, $"{at}: '{reference}.{req}' is optional but the input requires it", step);
            }
            foreach (var (field, fieldSchema) in targetShape.Properties)
                if (sourceShape.Properties.TryGetValue(field, out var sourceField))
                    CheckShape(at, path, step, reference + "." + field, sourceRoot, sourceField, targetRoot, fieldSchema, depth + 1);
        }

        // ── failure handlers ──
        // ── config ──
        private void CheckConfig()
        {
            for (var i = 0; i < m.Config.Count; i++)
            {
                var c = m.Config[i];
                var path = $"config[{i}]";
                if (!ConfigNameRegex().IsMatch(c.Name))
                    Error(ManifestIssueCodes.ConfigNameInvalid, $"{path}.name", $"config entry name '{c.Name}' must match {ConfigNameRegex()}");
                if (!_config.TryAdd(c.Name, c))
                    Error(ManifestIssueCodes.ConfigDuplicate, $"{path}.name", $"config entry '{c.Name}' is declared more than once");
                foreach (var secret in ConfigSecretRefs.Declared(c.Schema))
                    if (!_secrets.Contains(secret))
                        Error(ManifestIssueCodes.ConfigSecretRefUndeclared, $"{path}.schema",
                            $"config entry '{c.Name}' references secret '{secret}', which is not in the pipeline's secrets");
            }
        }

        private void CheckStepConfig(StepDescriptor s, string path, IReadOnlySet<string> stepSecrets)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < s.ConfigEntries.Count; i++)
            {
                var name = s.ConfigEntries[i];
                var cp = $"{path}.configEntries[{i}]";
                if (!seen.Add(name)) Error(ManifestIssueCodes.ConfigDuplicate, cp, $"step '{s.Name}' lists config entry '{name}' twice", s.Name);
                if (!_config.TryGetValue(name, out var entry))
                {
                    Error(ManifestIssueCodes.ConfigUnknown, cp, $"step '{s.Name}' uses config entry '{name}', which is not in the pipeline's config", s.Name);
                    continue;
                }
                // A library step that references parts of the entry needs the secrets those parts name; any other step
                // receiving the entry needs every secret it names.
                var refs = (s.Config ?? new Dictionary<string, JsonElement>())
                    .SelectMany(kv => ConfigRefs.Find(kv.Value, ""))
                    .Where(r => r.Reference.Split('.')[0] == name)
                    .ToList();
                var needed = refs.Count == 0
                    ? ConfigSecretRefs.Declared(entry.Schema)
                    : refs.SelectMany(r => ConfigRefs.SubSchemas(entry.Schema, r.Reference.Split('.')[1..], r.Case, name))
                        .SelectMany(ConfigSecretRefs.Declared).Distinct(StringComparer.Ordinal);
                foreach (var secret in needed)
                    if (!stepSecrets.Contains(secret))
                        Error(ManifestIssueCodes.ConfigSecretRefNotInStep, cp,
                            $"step '{s.Name}' receives config entry '{name}', which references secret '{secret}'; the step must also declare that secret", s.Name);
            }
        }

        /// <summary>Every <c>{"$config": "entry.path"}</c> in a library step's configuration names an entry the step receives, and a field it has.</summary>
        private void CheckConfigRefs(StepDescriptor s, string path, IReadOnlyList<CaseOf> cases)
        {
            if (s.Config is null) return;
            foreach (var (key, value) in s.Config)
                foreach (var (refPath, reference, condition) in ConfigRefs.Find(value, $"{path}.config.{key}"))
                {
                    if (condition is not null && !cases.Any(c => c.Condition == condition))
                        Error(ManifestIssueCodes.ConfigRefOutsideCase, refPath,
                            $"step '{s.Name}': config reference '{reference}' is narrowed to the select case '{condition}' and can only be used inside it", s.Name);
                    var segments = reference.Split('.');
                    var entryName = segments[0];
                    if (!s.ConfigEntries.Contains(entryName, StringComparer.Ordinal))
                        Error(ManifestIssueCodes.ConfigRefNotReceived, refPath,
                            $"step '{s.Name}': config reference '{reference}': the step does not receive config entry '{entryName}'", s.Name);
                    else if (_config.TryGetValue(entryName, out var entry)
                             && !JsonSchemaShapes.TryResolveField(entry.Schema, entry.Schema, segments[1..], out _))
                        Error(ManifestIssueCodes.ConfigRefFieldUnknown, refPath,
                            $"step '{s.Name}': config reference '{reference}': config '{entryName}' has no field '{string.Join('.', segments[1..])}'", s.Name);
                }
        }

        private void CheckFailureHandlers()
        {
            var handlers = new Dictionary<string, (string Path, string Canonical)>(StringComparer.Ordinal);
            for (var i = 0; i < m.FailureHandlers.Count; i++)
            {
                var fh = m.FailureHandlers[i];
                var path = $"failureHandlers[{i}]";
                var stepPath = $"{path}.step";
                var name = fh.Step.Name;
                var canonical = CanonicalJson.Serialize(ManifestWriter.ToElement(fh.Step));
                if (handlers.TryGetValue(name, out var seen))
                {
                    // The same handler may be attached several times; it must be the same step.
                    if (seen.Canonical != canonical)
                        Error(ManifestIssueCodes.FailureHandlerConflict, $"{stepPath}.name",
                            $"failure handler '{name}' differs from the one at {seen.Path} (one definition per step name)", name);
                }
                else
                {
                    handlers[name] = (stepPath, canonical);
                    CheckStepName(name, stepPath);
                    if (_stageSteps.TryGetValue(name, out var other))
                        Error(ManifestIssueCodes.StepNameDuplicate, $"{stepPath}.name", $"duplicate step name '{name}' (also at {other})", name);
                    CheckStep(fh.Step, stepPath, new Dictionary<string, Avail>(), failureHandler: true);
                }
                if (fh.Steps is null) continue;
                for (var j = 0; j < fh.Steps.Count; j++)
                    if (!_stageSteps.ContainsKey(fh.Steps[j]))
                        Error(ManifestIssueCodes.FailureHandlerStepUnknown, $"{path}.steps[{j}]",
                            $"failure handler '{name}': '{fh.Steps[j]}' is not a step of this pipeline", name);
            }
        }

        // ── warnings ──
        private void CheckUnused()
        {
            foreach (var (name, path) in _producers)
                if (!_consumedRoots.Contains(name))
                    Warn(ManifestIssueCodes.OutputUnused, $"{path}.output", $"the output of step '{name}' is not used by any later step (fine for gate steps)", name);
            foreach (var model in m.Models.Keys.Where(k => !_usedModels.Contains(k)))
                Warn(ManifestIssueCodes.ModelUnused, $"models.{model}", $"model '{model}' is not referenced by the pipeline");
        }
    }
}
