using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Olve.Pipelines.Configuration;

// `Results` alone resolves to the Olve.Results namespace (global using), so alias the ASP.NET one.
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace Olve.Pipelines.Distribution;

/// <summary>
/// Anonymous download endpoints for the <c>pl</c> operator CLI binaries. The pipeline's
/// <c>publish-cli</c> step builds the binaries and uploads them to this instance's MinIO under
/// <c>cli/latest/&lt;file&gt;</c>; this streams them straight back so an operator can bootstrap the
/// CLI with a single <c>curl</c> — before they have a token to authenticate with (hence anonymous).
/// </summary>
public static class CliDownloadEndpoints
{
    // Public download name -> object key. The allow-list is the security boundary: only these
    // names resolve, so the route can never be used to read arbitrary keys out of the bucket.
    private static readonly Dictionary<string, string> Assets = new(StringComparer.Ordinal)
    {
        ["pl-linux-x64"] = "cli/latest/pl-linux-x64",
        ["pl-win-x64.exe"] = "cli/latest/pl-win-x64.exe",
    };

    public static void MapCliDownloadEndpoints(this WebApplication app)
    {
        app.MapGet("/download/{asset}", async Task<IResult> (
            string asset,
            HttpContext http,
            IServiceProvider services,
            CancellationToken ct) =>
        {
            if (!Assets.TryGetValue(asset, out var key))
                return HttpResults.NotFound();

            // IAmazonS3 is only registered when storage is configured (it is not in the ephemeral
            // doc-generation / no-storage profiles) — resolve it optionally so the route degrades
            // to 503 instead of a DI resolution failure.
            if (services.GetService<IAmazonS3>() is not { } s3)
                return HttpResults.Problem("Storage not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);

            var storage = services.GetRequiredService<StorageOptions>();

            GetObjectResponse response;
            try
            {
                response = await s3.GetObjectAsync(new GetObjectRequest
                {
                    BucketName = storage.Bucket,
                    Key = key,
                }, ct);
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Binary not published yet (or wrong key) — surface as 404, not 500.
                return HttpResults.NotFound();
            }

            http.Response.ContentType = "application/octet-stream";
            http.Response.ContentLength = response.ContentLength >= 0 ? response.ContentLength : null;
            http.Response.Headers.ContentDisposition = $"attachment; filename=\"{asset}\"";

            // Stream straight from MinIO to the client — never buffer the (~70 MB) binary in the pod.
            using (response)
            {
                await response.ResponseStream.CopyToAsync(http.Response.Body, ct);
            }

            return HttpResults.Empty;
        })
        .AllowAnonymous()
        .WithName("DownloadCli");
    }
}
