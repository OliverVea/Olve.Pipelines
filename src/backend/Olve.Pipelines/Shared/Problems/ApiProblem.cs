using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Olve.Pipelines.Shared.Problems;

/// <summary>The body of every non-2xx API response (RFC 9457 style).</summary>
/// <param name="Type">A URI naming the error class, e.g. <c>urn:olve-pipelines:not-found</c>.</param>
/// <param name="Detail">The primary error's message.</param>
/// <param name="TraceId">Matches the <c>TraceId</c> of this request's log lines.</param>
public sealed record ApiProblem(
    string Type,
    string Title,
    int Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail,
    ApiProblemError[] Errors,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TraceId);

/// <param name="Field">The request field the error is about (e.g. <c>input.sha</c>), if any.</param>
public sealed record ApiProblemError(
    ApiProblemCode Code,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Field = null);

/// <summary>The error class; the problem's <c>type</c> is <c>urn:olve-pipelines:&lt;code&gt;</c> and its status follows from it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ApiProblemCode>))]
public enum ApiProblemCode
{
    [JsonStringEnumMemberName("not-found")] NotFound,
    [JsonStringEnumMemberName("conflict")] Conflict,
    [JsonStringEnumMemberName("invalid")] Invalid,
    [JsonStringEnumMemberName("unavailable")] Unavailable,
    [JsonStringEnumMemberName("internal")] Internal,
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ApiProblem))]
internal partial class ApiProblemJsonContext : JsonSerializerContext;

/// <summary>Builds <see cref="ApiProblem"/> bodies from problems or a bare status.</summary>
public static class ApiProblems
{
    public const string ContentType = "application/json";

    // Precedence: the most specific problem decides the status. A missing resource or a conflicting
    // state can't be fixed by retrying, so both outrank 503; plain problems are 400.
    private static readonly ErrorClass NotFound = new(StatusCodes.Status404NotFound, ApiProblemCode.NotFound, "Not found");
    private static readonly ErrorClass Conflict = new(StatusCodes.Status409Conflict, ApiProblemCode.Conflict, "Conflict");
    private static readonly ErrorClass Unavailable = new(StatusCodes.Status503ServiceUnavailable, ApiProblemCode.Unavailable, "Service unavailable");
    private static readonly ErrorClass Invalid = new(StatusCodes.Status400BadRequest, ApiProblemCode.Invalid, "Invalid request");
    private static readonly ErrorClass Internal = new(StatusCodes.Status500InternalServerError, ApiProblemCode.Internal, "Internal error");
    private static readonly ErrorClass[] Precedence = [NotFound, Conflict, Unavailable, Invalid];

    private sealed record ErrorClass(int Status, ApiProblemCode Code, string Title);

    private static ErrorClass ClassOf(ResultProblem problem) => problem switch
    {
        NotFoundProblem => NotFound,
        ConflictProblem => Conflict,
        UnavailableProblem => Unavailable,
        _ => Invalid,
    };

    /// <summary>The error class of a bare status code (framework-produced or exception responses).</summary>
    private static ErrorClass ClassOf(int status) => status switch
    {
        StatusCodes.Status404NotFound => NotFound,
        StatusCodes.Status409Conflict => Conflict,
        StatusCodes.Status503ServiceUnavailable => Unavailable,
        >= 500 => Internal with { Status = status },
        _ => Invalid with { Status = status, Title = ReasonPhrase(status) },
    };

    public static ApiProblem From(IReadOnlyCollection<ResultProblem> problems, string? traceId)
    {
        if (problems.Count == 0)
            return FromStatus(StatusCodes.Status500InternalServerError, "The request failed without a reason.", traceId);

        var classes = problems.Select(ClassOf).ToArray();
        var decisive = Precedence.First(c => classes.Contains(c));
        var errors = problems.Select(p => new ApiProblemError(ClassOf(p).Code, p.FormattedMessage, ProblemText.FieldOf(p))).ToArray();
        var detail = errors.First(e => e.Code == decisive.Code).Message;
        return new ApiProblem(TypeOf(decisive), decisive.Title, decisive.Status, detail, errors, traceId);
    }

    public static ApiProblem FromStatus(int status, string message, string? traceId)
    {
        var c = ClassOf(status);
        return new ApiProblem(TypeOf(c), c.Title, c.Status, message, [new ApiProblemError(c.Code, message)], traceId);
    }

    /// <summary>A problem answered by a contract handler, which has no <see cref="HttpContext"/>: the trace id comes from the current activity.</summary>
    public static ApiProblem Of(ResultProblem problem) => Of([problem]);

    /// <inheritdoc cref="Of(ResultProblem)"/>
    public static ApiProblem Of(IReadOnlyCollection<ResultProblem> problems) => From(problems, Activity.Current?.TraceId.ToHexString());

    /// <summary>The W3C trace id of the request (what the log scope carries), else ASP.NET's request id.</summary>
    public static string TraceId(HttpContext http) => Activity.Current?.TraceId.ToHexString() ?? http.TraceIdentifier;

    public static IResult ToResult(ApiProblem problem)
        => Microsoft.AspNetCore.Http.Results.Json(problem, ApiProblemJsonContext.Default.ApiProblem, ContentType, problem.Status);

    public static Task WriteAsync(HttpContext http, ApiProblem problem)
    {
        http.Response.StatusCode = problem.Status;
        return http.Response.WriteAsJsonAsync(problem, ApiProblemJsonContext.Default.ApiProblem, ContentType, http.RequestAborted);
    }

    private static string TypeOf(ErrorClass c) => $"urn:olve-pipelines:{Wire(c.Code)}";

    private static string Wire(ApiProblemCode code) => code switch
    {
        ApiProblemCode.NotFound => "not-found",
        ApiProblemCode.Conflict => "conflict",
        ApiProblemCode.Invalid => "invalid",
        ApiProblemCode.Unavailable => "unavailable",
        _ => "internal",
    };

    private static string ReasonPhrase(int status)
        => Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status) is { Length: > 0 } phrase ? phrase : "Invalid request";
}
