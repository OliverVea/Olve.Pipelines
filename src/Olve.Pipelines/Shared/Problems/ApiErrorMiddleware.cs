using System.Net;
using System.Net.Sockets;
using Amazon.Runtime;
using Microsoft.AspNetCore.Routing;

namespace Olve.Pipelines.Shared.Problems;

/// <summary>
/// Turns unhandled exceptions into <see cref="ApiProblem"/> bodies (transient dependency failures → 503
/// with <c>Retry-After</c>, bad requests → their status, the rest → 500) and gives empty-bodied
/// <c>/api</c> errors (unmatched methods, binding 404s, …) the same shape. 401/403 stay empty.
/// </summary>
public static class ApiErrorMiddleware
{
    private const int RetryAfterSeconds = 10;

    /// <summary>
    /// Binding failures throw so they get the problem body too; log scopes carry the TraceId that bodies return.
    /// </summary>
    public static IServiceCollection AddApiErrors(this IServiceCollection services) => services
        .Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true)
        .Configure<LoggerFactoryOptions>(o => o.ActivityTrackingOptions |=
            ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId | ActivityTrackingOptions.ParentId);

    public static IApplicationBuilder UseApiErrors(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        try
        {
            await next(http);
        }
        catch (Exception ex) when (http.RequestAborted.IsCancellationRequested
                                   || ex is OperationCanceledException && http.Response.HasStarted)
        {
            return; // the client is gone, or a stream ended on shutdown
        }
        catch (Exception ex)
        {
            await HandleAsync(http, ex);
            return;
        }

        if (NeedsBody(http))
            await ApiProblems.WriteAsync(http, ApiProblems.FromStatus(http.Response.StatusCode,
                StatusMessage(http.Response.StatusCode), ApiProblems.TraceId(http)));
    });

    private static async Task HandleAsync(HttpContext http, Exception ex)
    {
        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ApiErrorMiddleware).FullName!);
        if (http.Response.HasStarted)
        {
            logger.LogError(ex, "Unhandled exception on {Method} {Path} after the response started; aborting",
                http.Request.Method, http.Request.Path.Value);
            http.Abort();
            return;
        }

        int status;
        string message;
        if (ex is BadHttpRequestException bad)
        {
            status = bad.StatusCode;
            message = bad.Message; // framework text about the caller's own request
            logger.LogInformation("{Method} {Path} answered {StatusCode}: {Reason}", http.Request.Method, http.Request.Path.Value, status, bad.Message);
        }
        else if (IsTransient(ex))
        {
            status = StatusCodes.Status503ServiceUnavailable;
            message = "A dependency (storage, Kubernetes or GitHub) is unavailable; retry shortly.";
            logger.LogWarning(ex, "{Method} {Path} answered {StatusCode}: transient dependency failure", http.Request.Method, http.Request.Path.Value, status);
        }
        else
        {
            status = StatusCodes.Status500InternalServerError;
            message = "An unexpected error occurred.";
            logger.LogError(ex, "{Method} {Path} answered {StatusCode}: unhandled exception", http.Request.Method, http.Request.Path.Value, status);
        }

        http.Response.Clear();
        if (status == StatusCodes.Status503ServiceUnavailable)
            http.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await ApiProblems.WriteAsync(http, ApiProblems.FromStatus(status, message, ApiProblems.TraceId(http)));
    }

    /// <summary>Dependency failures a retry may fix: network errors, timeouts, 5xx/429 from Garage, K8s or GitHub.</summary>
    internal static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException h => h.StatusCode is null or >= HttpStatusCode.InternalServerError
            or HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout,
        AmazonServiceException s => s.StatusCode is >= HttpStatusCode.InternalServerError or HttpStatusCode.TooManyRequests
            || s.InnerException is not null && IsTransient(s.InnerException),
        AmazonClientException => true,
        FileNotFoundException or DirectoryNotFoundException => false,
        TimeoutException or OperationCanceledException or SocketException or IOException => true,
        _ => ex.InnerException is { } inner && IsTransient(inner),
    };

    private static bool NeedsBody(HttpContext http)
    {
        var response = http.Response;
        return response.StatusCode >= 400
               && response.StatusCode is not (StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden)
               && !response.HasStarted
               && response.ContentLength is null
               && string.IsNullOrEmpty(response.ContentType)
               && !HttpMethods.IsHead(http.Request.Method)
               && http.Request.Path.StartsWithSegments("/api");
    }

    private static string StatusMessage(int status) => status switch
    {
        StatusCodes.Status404NotFound => "No such resource.",
        StatusCodes.Status503ServiceUnavailable => "The service is not ready; retry shortly.",
        _ => Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status) is { Length: > 0 } p ? p + "." : "The request failed.",
    };
}
