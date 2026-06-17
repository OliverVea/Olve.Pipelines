using Olve.MinimalApi;

namespace Olve.Pipelines.Pipelines.Sync;

public static class PipelineDocumentEndpoints
{
    public static void MapPipelineDocumentEndpoints(this WebApplication app)
    {
        // Read-side export only. There is no from-document create path: GitOps reconcile is the
        // sole writer of pipeline shape, so a document can be exported but not applied via the API.
        app.MapGet("/api/pipelines/{pipelineId}/document",
                Result<PipelineDocument> (PipelineDocumentBuilder builder, Id<Pipeline> pipelineId)
                    => builder.Build(pipelineId))
            .WithResultMapping<PipelineDocument>()
            .WithName("GetPipelineDocument")
            .WithTags("beta");
    }
}
