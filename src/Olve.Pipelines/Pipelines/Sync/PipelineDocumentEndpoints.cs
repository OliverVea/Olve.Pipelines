using Olve.MinimalApi;

namespace Olve.Pipelines.Pipelines.Sync;

public static class PipelineDocumentEndpoints
{
    public static void MapPipelineDocumentEndpoints(this WebApplication app)
    {
        app.MapGet("/api/pipelines/{pipelineId}/document",
                Result<PipelineDocument> (PipelineDocumentBuilder builder, Id<Pipeline> pipelineId)
                    => builder.Build(pipelineId))
            .WithResultMapping<PipelineDocument>()
            .WithName("GetPipelineDocument")
            .WithTags("beta");
    }
}
