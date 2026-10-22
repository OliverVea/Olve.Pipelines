namespace Olve.Pipelines.Shared.Problems;

/// <summary>
/// Maps endpoint <see cref="Result"/>s to HTTP: success → 200 with the value, failure → an
/// <see cref="ApiProblem"/> (status by <see cref="ApiProblems"/> precedence). Replaces Olve.MinimalApi's
/// <c>WithResultMapping</c>, whose body is a bare <c>ResultProblem[]</c>.
/// </summary>
public static class ApiResultMapping
{
    public static RouteHandlerBuilder WithApiResultMapping<T>(this RouteHandlerBuilder builder)
        => builder.Produces<T>().WithApiProblems().AddEndpointFilter(MapResult);

    public static RouteHandlerBuilder WithApiResultMapping(this RouteHandlerBuilder builder)
        => builder.Produces(StatusCodes.Status200OK).WithApiProblems().AddEndpointFilter(MapResult);

    /// <summary>Success → 200, not found → 404, other problems → their <see cref="ApiProblem"/>.</summary>
    public static RouteHandlerBuilder WithDeletionMapping(this RouteHandlerBuilder builder)
        => builder.Produces(StatusCodes.Status200OK).WithApiProblems().AddEndpointFilter(async (context, next) =>
            await next(context) switch
            {
                DeletionResult { Succeeded: true } => TypedResults.Ok(),
                DeletionResult { WasNotFound: true } => context.HttpContext.ToApiResult(
                    [new NotFoundProblem("Nothing to delete at '{0}'.", context.HttpContext.Request.Path.Value ?? "")]),
                DeletionResult d => context.HttpContext.ToApiResult(d.Problems?.ToArray() ?? []),
                var other => other,
            });

    /// <summary>Declares the <see cref="ApiProblem"/> error responses in OpenAPI.</summary>
    public static RouteHandlerBuilder WithApiProblems(this RouteHandlerBuilder builder)
    {
        foreach (var status in (int[])[400, 404, 409, 500, 503])
            builder.Produces<ApiProblem>(status, ApiProblems.ContentType);
        return builder;
    }

    /// <summary>The failure response for <paramref name="problems"/>; logs them once, with their internals.</summary>
    public static IResult ToApiResult(this HttpContext http, IReadOnlyCollection<ResultProblem> problems)
    {
        var problem = ApiProblems.From(problems, ApiProblems.TraceId(http));
        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ApiResultMapping).FullName!);
        logger.LogProblems(problem.Status >= 500 ? LogLevel.Warning : LogLevel.Information, problems,
            "{Method} {Path} answered {StatusCode}", http.Request.Method, http.Request.Path.Value, problem.Status);
        return ApiProblems.ToResult(problem);
    }

    private static async ValueTask<object?> MapResult(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        => await next(context) switch
        {
            IResultType { Failed: true } r => context.HttpContext.ToApiResult(r.Problems?.ToArray() ?? []),
            IResultType { HasValue: true } r => TypedResults.Ok(r.BoxedValue),
            IResultType => TypedResults.Ok(),
            var other => other,
        };
}
