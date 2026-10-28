using Olve.Pipelines.Configuration;

namespace Olve.Pipelines.Pipelines.Runs;

/// <summary>The one run endpoint outside the contract: a binary file download.</summary>
public static class RunEndpoints
{
    public static void MapRunEndpoints(this WebApplication app)
    {
        // Downloads one file by its §5.5 address #run/step[@attempt]/path (a directory ref "dir/" yields dir.tar).
        app.MapGet("/api/pipelines/{pipelineId}/runs/{number:int}/steps/{step}/files/{*path}", async Task<IResult> (
                HttpContext http, RunQueryService query, AttemptFileService files, Id<Pipeline> pipelineId, int number, string step,
                string path, int? attempt, CancellationToken ct) =>
            {
                if (AttemptFileService.ToObjectPath(path).TryPickProblems(out var pathProblems, out var objectPath))
                    return http.ToApiResult(pathProblems.ToArray());
                if (!query.TryResolve(pipelineId, number, step, attempt, out var resolved))
                    return http.ToApiResult([ListStepFilesHandler.NoAttempt(number, step, attempt)]);

                var download = await files.OpenAsync(resolved, objectPath, ct);
                if (download is null)
                    return http.ToApiResult([new NotFoundProblem("Attempt {0} of step '{1}' in run #{2} has no file '{3}'.",
                        resolved.Number, step, number, objectPath)]);

                http.Response.RegisterForDispose(download);
                if (download.Length is { } length)
                    http.Response.ContentLength = length;
                return Microsoft.AspNetCore.Http.Results.Stream(download.Content, download.ContentType, download.FileName);
            })
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .WithApiProblems()
            .WithName("GetStepFile")
            .WithTags("Steps")
            .WithSummary("Download a step file")
            .WithDescription("One stored file; a directory `dir/` downloads as `dir.tar`.");
    }
}
