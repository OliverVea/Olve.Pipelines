using Olve.Pipelines.Shared;

namespace Olve.Pipelines.FailureHandlers;

/// <summary>
/// The built-in, named library of failure-handler scripts. A handler is just a
/// <see cref="StepConfiguration"/> (image + script) — it reuses the same execution machinery as a
/// normal step. A pipeline references one by name and supplies the handler-config env (class (b))
/// in its binding; the orchestrator adds the <c>PIPELINE_*</c> failure context (class (a)). The
/// library ships in the app so it deploys with it and is reusable across pipelines.
/// </summary>
public class FailureHandlerLibrary
{
    /// <summary>Opens an Agent of Empires session to triage a pipeline failure.</summary>
    public const string AoeTriage = "aoe-triage";

    private static readonly Dictionary<string, StepConfiguration> Handlers = new()
    {
        [AoeTriage] = new StepConfiguration(
            // alpine + the three tools the script needs: curl (POST), jq (build the JSON body
            // safely), envsubst (expand $PIPELINE_* inside the operator-supplied prompt template).
            Image: "alpine:3.20",
            Script: AoeTriageScript),
    };

    public bool TryGet(string name, out StepConfiguration configuration)
        => Handlers.TryGetValue(name, out configuration!);

    public IReadOnlyCollection<string> Names => Handlers.Keys;

    // The template arrives as a literal env value, so its $PIPELINE_* references are NOT expanded by
    // the shell — envsubst expands them against the injected failure-context env. jq -n builds the
    // body so a reason/prompt containing quotes can't break the JSON. AOE_BASE_URL is the in-cluster
    // Service URL (handlers run inside the cluster, below the Authentik ingress), supplied by config.
    private const string AoeTriageScript =
        """
        #!/bin/sh
        # aoe-triage: open an Agent of Empires session to investigate a pipeline failure.
        set -eu
        apk add --no-cache curl jq gettext >/dev/null
        prompt=$(printf '%s' "$AOE_PROMPT_TEMPLATE" | envsubst)
        curl -fsS -X POST "$AOE_BASE_URL/api/sessions" \
          -H 'Content-Type: application/json' \
          -d "$(jq -n \
                --arg path "$AOE_REPO_PATH" \
                --arg tool "${AOE_TOOL:-claude}" \
                --arg title "triage-$PIPELINE_NAME-$PIPELINE_FAILED_STEP" \
                --arg instr "$prompt" \
                '{path:$path, tool:$tool, title:$title, custom_instruction:$instr,
                  create_new_branch:true, yolo_mode:true, trust_hooks:true}')"
        """;
}
