using Olve.MinimalApi;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Pipelines.Triggers;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>
/// Web-layer composition of "create pipeline + bind to repo". The binding depends DOWN on the
/// pipeline, so this composition lives here, not in <see cref="PipelineService"/>. Binding a
/// pipeline to a repo is what configures the deploy poll by default — no manual poll-trigger
/// authoring (the job <c>setup-pipeline</c> Option A used to do by hand).
/// </summary>
public static class PipelineBindingEndpoints
{
    private const string DefaultBranch = "main";
    private const string DefaultPath = ".pipelines";

    public static void MapPipelineBindingEndpoints(this WebApplication app)
    {
        // Create a pipeline already bound to a repo. Rolls the pipeline back if binding fails,
        // so a failed bind never leaves an orphan draft.
        app.MapPost("/api/pipelines/with-repo", Result<PipelineConfigBinding> (
                PipelineService pipelines,
                PipelineConfigBindingService bindings,
                CreatePipelineWithRepoRequest request) =>
            {
                var pipelineResult = pipelines.Create(request.Name);
                if (pipelineResult.TryPickProblems(out var pipelineProblems, out var pipeline))
                    return pipelineProblems;

                var bindingResult = bindings.Create(
                    pipeline.Id, request.Repo, Branch(request.Branch), Path(request.Path), request.CredentialsSecret,
                    request.DeployTrigger ?? BindingDeployTrigger.Webhook);

                if (bindingResult.TryPickProblems(out var bindingProblems))
                {
                    pipelines.Delete(pipeline.Id);
                    return bindingProblems;
                }

                return bindingResult;
            })
            .WithResultMapping<PipelineConfigBinding>()
            .WithName("CreatePipelineWithRepo")
            .WithTags("beta");

        // Reconcile the bound config immediately, off the poll schedule. The deploy poll only runs
        // every ReconcileOptions.PollInterval (15 min in prod), so a just-bound pipeline would
        // otherwise wait up to a full interval for its shape to materialize. This applies the
        // current branch config now; the response returns after the reconcile completes.
        app.MapPost("/api/pipelines/{pipelineId}/binding/reconcile", async Task<Result> (
                DeployPollService deployPoll, Id<Pipeline> pipelineId, CancellationToken ct)
                => await deployPoll.ReconcileNowAsync(pipelineId, ct))
            .WithResultMapping()
            .WithName("ReconcilePipelineBinding")
            .WithTags("beta");

        // Update an existing binding's credentials secret without re-binding. Setting it moves the
        // config fetch onto the authenticated GitHub bucket (5000/hr vs the unauthenticated 60/hr);
        // an empty value clears it. The named key must exist in the pipeline's k8s secret.
        app.MapPatch("/api/pipelines/{pipelineId}/binding", Result<PipelineConfigBinding> (
                PipelineConfigBindingService bindings, Id<Pipeline> pipelineId, UpdateBindingRequest request)
                => bindings.SetCredentialsSecret(pipelineId, request.CredentialsSecret))
            .WithResultMapping<PipelineConfigBinding>()
            .WithName("UpdatePipelineBinding")
            .WithTags("beta");

        // Change how the bound pipeline is triggered (webhook / webhook-only / poll) without
        // re-binding. A dedicated route (not the credentials PATCH) so neither field clobbers the
        // other. Switching into a webhook mode generates the HMAC secret if absent; the binding
        // event then drives hook (de)registration.
        app.MapPatch("/api/pipelines/{pipelineId}/binding/deploy-trigger", Result<PipelineConfigBinding> (
                PipelineConfigBindingService bindings, Id<Pipeline> pipelineId, SetDeployTriggerRequest request)
                => bindings.SetDeployTrigger(pipelineId, request.DeployTrigger))
            .WithResultMapping<PipelineConfigBinding>()
            .WithName("UpdatePipelineBindingDeployTrigger")
            .WithTags("beta");

        // Inbound GitHub push webhook for a binding (webhook-mode deploy). Authenticated by HMAC over
        // the raw body (not Bearer), so it binds HttpRequest and returns raw status codes. A matching
        // push runs the binding's reconcile-then-deploy cycle. Exposed on the public ingress like the
        // per-pipeline GitHub receiver.
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

                var logger = loggerFactory.CreateLogger("BindingWebhook");
                var result = await deployPoll.ReconcileNowAsync(pipelineId, ct);
                if (result.TryPickProblems(out var problems))
                {
                    logger.LogWarning("Binding webhook for '{BindingId}' did not deploy: {Problems}", bindingId, problems);
                    return HttpResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }

                logger.LogInformation("Binding webhook for '{BindingId}' ran reconcile+deploy", bindingId);
                return HttpResults.Accepted();
            })
            .WithName("ExecuteBindingGitHubWebhook")
            .AllowAnonymous();

        app.MapGet("/api/pipelines/{pipelineId}/binding", Result<PipelineConfigBinding> (
                PipelineConfigBindingService bindings, Id<Pipeline> pipelineId)
                => bindings.GetByPipelineId(pipelineId))
            .WithResultMapping<PipelineConfigBinding>()
            .WithName("GetPipelineBinding")
            .WithTags("beta")
            .AllowAnonymous();

        // Reconcile status + live secret set/unset for the frontend badge.
        app.MapGet("/api/pipelines/{pipelineId}/binding/status", async Task<Result<PipelineBindingStatus>> (
                PipelineConfigBindingService bindings,
                IServiceProvider services,
                ILoggerFactory loggerFactory,
                Id<Pipeline> pipelineId,
                CancellationToken ct) =>
            {
                if (bindings.GetByPipelineId(pipelineId).TryPickProblems(out var problems, out var binding))
                    return problems;

                // Compute set/unset live so a just-set secret reflects immediately. Resolve the k8s
                // client lazily and guard it: when k8s is unconfigured/unreachable, report unknown
                // (IsSet null) rather than a misleading "unset" — and never 500 the status read.
                Dictionary<string, string>? secret = null;
                var secretsKnown = true;
                try
                {
                    var kubernetes = services.GetRequiredService<KubernetesClient>();
                    var kubernetesOptions = services.GetRequiredService<KubernetesOptions>();
                    secret = await kubernetes.GetSecretAsync(kubernetesOptions.Namespace, SecretName(pipelineId), ct);
                }
                catch (Exception ex)
                {
                    secretsKnown = false;
                    loggerFactory.CreateLogger("BindingStatus")
                        .LogWarning(ex, "Could not read secrets for pipeline '{PipelineId}'", pipelineId);
                }

                var secretStatuses = binding.Status.DeclaredSecrets
                    .Select(s => new SecretStatus(
                        s.Name, s.Description,
                        IsSet: secretsKnown ? secret is not null && secret.ContainsKey(s.Name) : null))
                    .ToArray();

                return new PipelineBindingStatus(
                    binding.PipelineId, binding.Repo, binding.Branch, binding.Path,
                    binding.LastDeployedSha, binding.LastSyncedSha,
                    binding.Status.Result, binding.Status.LastSyncTime, binding.Status.Problems,
                    secretStatuses);
            })
            .WithResultMapping<PipelineBindingStatus>()
            .WithName("GetPipelineBindingStatus")
            .WithTags("beta")
            .AllowAnonymous();
    }

    private static string SecretName(Id<Pipeline> pipelineId) => $"olve-pipeline-{pipelineId.Value.Value:N}";

    private static string Branch(string? branch) => string.IsNullOrWhiteSpace(branch) ? DefaultBranch : branch;
    private static string Path(string? path) => string.IsNullOrWhiteSpace(path) ? DefaultPath : path;
}

public record CreatePipelineWithRepoRequest(
    string Name, string Repo, string? Branch, string? Path, string? CredentialsSecret,
    BindingDeployTrigger? DeployTrigger = null);

public record UpdateBindingRequest(string? CredentialsSecret);

public record SetDeployTriggerRequest(BindingDeployTrigger DeployTrigger);

public record PipelineBindingStatus(
    Id<Pipeline> PipelineId,
    string Repo,
    string Branch,
    string Path,
    string? LastDeployedSha,
    string? LastSyncedSha,
    ReconcileResult Result,
    DateTimeOffset? LastSyncTime,
    IReadOnlyList<string> Problems,
    IReadOnlyList<SecretStatus> Secrets);

/// <summary><see cref="IsSet"/> is null when k8s could not be read (unknown), not false.</summary>
public record SecretStatus(string Name, string? Description, bool? IsSet);
