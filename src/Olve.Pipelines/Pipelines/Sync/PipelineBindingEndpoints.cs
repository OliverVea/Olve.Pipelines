using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.GitHub;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>Binding API: create a pipeline bound to a repo, reconcile now, update credentials/trigger,
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
                    pipeline.Id, request.Repo, Branch(request.Branch), Path(request.Path), request.CredentialsSecret,
                    request.DeployTrigger ?? BindingDeployTrigger.Webhook);

                if (bindingResult.TryPickProblems(out var bindingProblems))
                {
                    pipelines.Delete(pipeline.Id).DiscardResult(); // roll back the just-created pipeline; NotFound is benign
                    return bindingProblems;
                }

                return bindingResult;
            })
            .WithApiResultMapping<PipelineConfigBinding>()
            .WithName("CreatePipelineWithRepo")
;

        // Returns at once; the reconcile runs in the background (progress in GET .../binding/status).
        // Also retries a commit whose reconcile failed, which the poll loop never does.
        app.MapPost("/api/pipelines/{pipelineId}/binding/reconcile", Result (
                DeployPollService deployPoll, Id<Pipeline> pipelineId)
                => deployPoll.RequestReconcile(pipelineId, force: true))
            .WithApiResultMapping()
            .WithName("ReconcilePipelineBinding")
;

        // The credentials key moves GitHub reads onto the authenticated rate limit; empty clears it.
        app.MapPatch("/api/pipelines/{pipelineId}/binding", Result<PipelineConfigBinding> (
                PipelineConfigBindingService bindings, Id<Pipeline> pipelineId, UpdateBindingRequest request)
                => bindings.SetCredentialsSecret(pipelineId, request.CredentialsSecret))
            .WithApiResultMapping<PipelineConfigBinding>()
            .WithName("UpdatePipelineBinding")
;

        // Separate from the credentials PATCH so neither field clobbers the other.
        app.MapPatch("/api/pipelines/{pipelineId}/binding/deploy-trigger", Result<PipelineConfigBinding> (
                PipelineConfigBindingService bindings, Id<Pipeline> pipelineId, SetDeployTriggerRequest request)
                => bindings.SetDeployTrigger(pipelineId, request.DeployTrigger))
            .WithApiResultMapping<PipelineConfigBinding>()
            .WithName("UpdatePipelineBindingDeployTrigger")
;

        // Authenticated by HMAC over the raw body (not Bearer), so it binds HttpRequest and returns raw
        // status codes. The only route on the public ingress.
        app.MapPost("/api/webhooks/binding/{bindingId}/github", async Task<IResult> (
                BindingWebhookReceiver receiver,
                DeployPollService deployPoll,
                ILoggerFactory loggerFactory,
                Id<PipelineConfigBinding> bindingId,
                HttpRequest httpRequest,
                CancellationToken ct) =>
            {
                using var buffer = new MemoryStream();
                await httpRequest.Body.CopyToAsync(buffer, ct);
                var body = buffer.ToArray();

                var signature = httpRequest.Headers[GitHubWebhookSignature.HeaderName].ToString();
                var eventType = httpRequest.Headers["X-GitHub-Event"].ToString();

                var (action, pipelineId) = receiver.Evaluate(bindingId, body, signature, eventType);
                switch (action)
                {
                    case BindingWebhookAction.NotFound:
                        return HttpResults.NotFound();
                    case BindingWebhookAction.InvalidSignature:
                        return HttpResults.Unauthorized();
                    case BindingWebhookAction.Ignore:
                        return HttpResults.NoContent();
                }

                // GitHub gives a delivery 10 s: queue the reconcile + push trigger and acknowledge.
                var logger = loggerFactory.CreateLogger("BindingWebhook");
                if (deployPoll.RequestReconcile(pipelineId, force: false).TryPickProblems(out var problems))
                {
                    logger.LogProblems(LogLevel.Warning, problems, "Binding webhook for '{BindingId}' could not queue a reconcile", bindingId);
                    return HttpResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }

                logger.LogInformation("Binding webhook for '{BindingId}' queued reconcile + push trigger", bindingId);
                return HttpResults.Accepted();
            })
            .WithName("ExecuteBindingGitHubWebhook")
            .AllowAnonymous();

        app.MapGet("/api/pipelines/{pipelineId}/binding", Result<PipelineConfigBinding> (
                PipelineConfigBindingService bindings, Id<Pipeline> pipelineId)
                => bindings.GetByPipelineId(pipelineId))
            .WithApiResultMapping<PipelineConfigBinding>()
            .WithName("GetPipelineBinding")
            .AllowAnonymous();

        app.MapGet("/api/pipelines/{pipelineId}/binding/status", Result<PipelineBindingStatus> (
                PipelineConfigBindingService bindings, PipelineActivationService activations, PipelineVersionService versions,
                Id<Pipeline> pipelineId) =>
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
                    activations.TryGet(pipelineId, out var activation) ? activation : null);
            })
            .WithApiResultMapping<PipelineBindingStatus>()
            .WithName("GetPipelineBindingStatus")
            .AllowAnonymous();
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
}

public record CreatePipelineWithRepoRequest(
    string Repo, string? Branch, string? Path, string? CredentialsSecret,
    BindingDeployTrigger? DeployTrigger = null);

public record UpdateBindingRequest(string? CredentialsSecret);

public record SetDeployTriggerRequest(BindingDeployTrigger DeployTrigger);

/// <param name="ReconcileSha">The config commit the last reconcile attempt was for.</param>
/// <param name="ReconciledVersion">The version the last successful reconcile switched to.</param>
/// <param name="Activation">The pipeline's active version, or the switch in progress.</param>
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
    PipelineActivation? Activation);
