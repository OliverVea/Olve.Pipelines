using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines.Configs;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.GitHub;
using Olve.Pipelines.Sources;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>Binding API: create a pipeline bound to a repo, reconcile now, update credentials,
/// status, and the inbound GitHub push webhook.</summary>
public static class PipelineBindingEndpoints
{
    private const string DefaultBranch = "main";
    private const string DefaultPath = ".pipelines";

    public static void MapPipelineBindingEndpoints(this WebApplication app)
    {
        // Rolls the pipeline back if binding fails, so a failed bind never leaves an orphan.
        app.MapPost("/api/pipelines/with-repo", Result<PipelineConfigBinding> (
                PipelineService pipelines,
                PipelineConfigBindingService bindings,
                CreatePipelineWithRepoRequest request) =>
            {
                var pipelineResult = pipelines.Create(ProvisionalName(request.Repo));
                if (pipelineResult.TryPickProblems(out var pipelineProblems, out var pipeline))
                    return pipelineProblems;

                var bindingResult = bindings.Create(
                    pipeline.Id, request.Repo, Branch(request.Branch), Path(request.Path), request.CredentialsSecret);

                if (bindingResult.TryPickProblems(out var bindingProblems))
                {
                    pipelines.Delete(pipeline.Id).DiscardResult(); // roll back the just-created pipeline; NotFound is benign
                    return bindingProblems;
                }

                return Redacted(bindingResult);
            })
            .WithApiResultMapping<PipelineConfigBinding>()
            .WithName("CreatePipelineWithRepo")
            .WithTags("Bindings")
            .WithSummary("Create a pipeline bound to a repo")
            .WithDescription("The only way to create a pipeline. The binding watches `path` (default `.pipelines`) on `branch` of `repo`; `credentialsSecret` names the pipeline-secret key holding a GitHub token.")
;

        // Returns at once; the reconcile runs in the background (progress in GET .../binding/status).
        // Also retries a commit whose reconcile failed, which the poll loop never does.
        app.MapPost("/api/pipelines/{pipelineId}/binding/reconcile", Result (
                DeployPollService deployPoll, Id<Pipeline> pipelineId)
                => deployPoll.RequestReconcile(pipelineId, force: true))
            .WithApiResultMapping()
            .WithName("ReconcilePipelineBinding")
            .WithTags("Bindings")
            .WithSummary("Reconcile now")
            .WithDescription("Queues a reconcile of the bound config and returns at once; progress shows in the binding status. Also retries a failed commit.")
;

        // The credentials key moves GitHub reads onto the authenticated rate limit; empty clears it.
        app.MapPatch("/api/pipelines/{pipelineId}/binding", Result<PipelineConfigBinding> (
                PipelineConfigBindingService bindings, Id<Pipeline> pipelineId, UpdateBindingRequest request)
                => Redacted(bindings.SetCredentialsSecret(pipelineId, request.CredentialsSecret)))
            .WithApiResultMapping<PipelineConfigBinding>()
            .WithName("UpdatePipelineBinding")
            .WithTags("Bindings")
            .WithSummary("Update the binding")
            .WithDescription("Sets or clears the credentials secret key.")
;

        app.MapPut("/api/pipelines/{pipelineId}/binding/branch", Result<PipelineConfigBinding> (
                PipelineConfigBindingService bindings, Id<Pipeline> pipelineId, SetBindingBranchRequest request)
                => Redacted(bindings.SetBranch(pipelineId, request.Branch)))
            .WithApiResultMapping<PipelineConfigBinding>()
            .WithName("SetPipelineBindingBranch")
            .WithTags("Bindings")
            .WithSummary("Move the binding to another branch")
            .WithDescription("Binds the pipeline to another branch of the same repo, keeping its runs, versions and secrets. The next poll reconciles if that branch's newest config commit differs, and fires the push trigger if its head differs from the last push.")
;

        // Authenticated by HMAC over the raw body (not Bearer), so it binds HttpRequest and returns raw
        // status codes. On the public ingress, with /api/hooks.
        app.MapPost("/api/webhooks/binding/{bindingId}/github", async Task<IResult> (
                BindingWebhookReceiver receiver,
                PushTriggerService pushTriggers,
                ILoggerFactory loggerFactory,
                TimeProvider time,
                Id<PipelineConfigBinding> bindingId,
                HttpRequest httpRequest,
                CancellationToken ct) =>
            {
                if ((await WebhookDelivery.ReadAsync(httpRequest, ct)).TryPickProblems(out _, out var delivery))
                    return HttpResults.StatusCode(StatusCodes.Status413PayloadTooLarge);

                var receivedAt = time.GetUtcNow();
                var (action, pipelineId, push) = receiver.Evaluate(
                    bindingId, delivery.Body, delivery.Header(GitHubWebhookSignature.HeaderName), delivery.Header("X-GitHub-Event"));
                switch (action)
                {
                    case BindingWebhookAction.NotFound:
                        return HttpResults.NotFound();
                    case BindingWebhookAction.InvalidSignature:
                        return HttpResults.Unauthorized();
                    case BindingWebhookAction.Ignore:
                        return HttpResults.NoContent();
                }

                // GitHub gives a delivery 10 s: queue the push check (config first) and acknowledge.
                var logger = loggerFactory.CreateLogger("BindingWebhook");
                if (pushTriggers.RequestCheck(pipelineId, new PushNotice(receivedAt, push)).TryPickProblems(out var problems))
                {
                    logger.LogProblems(LogLevel.Warning, problems, "Binding webhook for '{BindingId}' could not queue a push check", bindingId);
                    return HttpResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }

                logger.LogInformation("Binding webhook for '{BindingId}' queued a push check (config first)", bindingId);
                return HttpResults.Accepted();
            })
            .WithName("ExecuteBindingGitHubWebhook")
            .WithTags("Bindings")
            .WithSummary("GitHub push webhook")
            .WithDescription("Receives the binding's GitHub push webhook. Anonymous, verified with the webhook's HMAC signature.")
            .AllowAnonymous();

        app.MapGet("/api/pipelines/{pipelineId}/binding", Result<PipelineConfigBinding> (
                PipelineConfigBindingService bindings, Id<Pipeline> pipelineId)
                => Redacted(bindings.GetByPipelineId(pipelineId)))
            .WithApiResultMapping<PipelineConfigBinding>()
            .WithName("GetPipelineBinding")
            .WithTags("Bindings")
            .WithSummary("Get the binding")
            .RequireRead();

        app.MapGet("/api/pipelines/{pipelineId}/binding/status", Result<PipelineBindingStatus> (
                PipelineConfigBindingService bindings, PipelineActivationService activations, PipelineVersionService versions,
                ConfigActivationService gate, Id<Pipeline> pipelineId) =>
            {
                if (bindings.GetByPipelineId(pipelineId).TryPickProblems(out var problems, out var binding))
                    return problems;

                var status = binding.Status;
                return new PipelineBindingStatus(
                    binding.PipelineId, binding.Repo, binding.Branch, binding.Path,
                    binding.LastPushSha, binding.LastReconciledSha,
                    status.Result, status.LastSyncTime, status.Problems, status.Warnings ?? [],
                    status.Sha, status.InProgress,
                    // Snapshots from before VersionNumber existed: the version built from the reconciled commit.
                    status.VersionNumber ?? (status.Result == ReconcileResult.Success
                        ? versions.ListByPipeline(pipelineId).FirstOrDefault(v => v.CommitSha == status.Sha)?.Number
                        : null),
                    activations.TryGet(pipelineId, out var activation) ? activation : null,
                    gate.PendingInfo(pipelineId));
            })
            .WithApiResultMapping<PipelineBindingStatus>()
            .WithName("GetPipelineBindingStatus")
            .WithTags("Bindings")
            .WithSummary("Get the binding's reconcile status")
            .WithDescription("The last reconcile's result and commit, whether one is in progress, the resulting and the active version, problems and warnings.")
            .RequireRead();
    }

    // Until the first reconcile sets the manifest's name: "OliverVea/Olve.Pipelines" -> "Olve.Pipelines".
    private static string ProvisionalName(string repo)
    {
        var trimmed = repo.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        var segment = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        return string.IsNullOrWhiteSpace(segment) ? repo : segment;
    }

    private static string Branch(string? branch) => string.IsNullOrWhiteSpace(branch) ? DefaultBranch : branch;
    private static string Path(string? path) => string.IsNullOrWhiteSpace(path) ? DefaultPath : path;

    // The webhook HMAC key never leaves the service (the binding GET is anonymous).
    internal static Result<PipelineConfigBinding> Redacted(Result<PipelineConfigBinding> binding) =>
        binding.TryPickProblems(out var problems, out var value) ? problems : value with { WebhookSecret = null };
}

public record CreatePipelineWithRepoRequest(string Repo, string? Branch, string? Path, string? CredentialsSecret);

public record UpdateBindingRequest(string? CredentialsSecret);

public record SetBindingBranchRequest(string Branch);

/// <param name="ReconcileSha">The config commit the last reconcile attempt was for.</param>
/// <param name="ReconciledVersion">The version the last successful reconcile switched to.</param>
/// <param name="Activation">The pipeline's active version, or the switch in progress.</param>
/// <param name="Pending">A version reconcile produced that waits for its config or secrets, with the reasons.</param>
public record PipelineBindingStatus(
    Id<Pipeline> PipelineId,
    string Repo,
    string Branch,
    string Path,
    string? LastPushSha,
    string? LastReconciledSha,
    ReconcileResult Result,
    DateTimeOffset? LastSyncTime,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Warnings,
    string? ReconcileSha,
    bool ReconcileInProgress,
    int? ReconciledVersion,
    PipelineActivation? Activation,
    PendingVersionInfo? Pending = null);
