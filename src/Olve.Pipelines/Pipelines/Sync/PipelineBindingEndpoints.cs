using Olve.MinimalApi;
using Olve.Pipelines.Kubernetes;

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
                    pipeline.Id, request.Repo, Branch(request.Branch), Path(request.Path), request.CredentialsSecret);

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

        // Bind an existing draft pipeline to a repo.
        app.MapPost("/api/pipelines/{pipelineId}/binding", Result<PipelineConfigBinding> (
                PipelineService pipelines,
                PipelineConfigBindingService bindings,
                Id<Pipeline> pipelineId,
                BindRepoRequest request) =>
            {
                if (!pipelines.TryGet(pipelineId, out _))
                    return Result.Failure<PipelineConfigBinding>(new ResultProblem($"Pipeline '{pipelineId}' not found."));

                return bindings.Create(
                    pipelineId, request.Repo, Branch(request.Branch), Path(request.Path), request.CredentialsSecret);
            })
            .WithResultMapping<PipelineConfigBinding>()
            .WithName("BindPipelineToRepo")
            .WithTags("beta");

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
    string Name, string Repo, string? Branch, string? Path, string? CredentialsSecret);

public record BindRepoRequest(string Repo, string? Branch, string? Path, string? CredentialsSecret);

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
