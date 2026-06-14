using Olve.MinimalApi;

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
    }

    private static string Branch(string? branch) => string.IsNullOrWhiteSpace(branch) ? DefaultBranch : branch;
    private static string Path(string? path) => string.IsNullOrWhiteSpace(path) ? DefaultPath : path;
}

public record CreatePipelineWithRepoRequest(
    string Name, string Repo, string? Branch, string? Path, string? CredentialsSecret);

public record BindRepoRequest(string Repo, string? Branch, string? Path, string? CredentialsSecret);
