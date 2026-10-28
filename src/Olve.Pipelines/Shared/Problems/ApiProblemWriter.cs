using Olve.TypeSpec;

namespace Olve.Pipelines.Shared.Problems;

/// <summary>
/// Answers the generated API's binding and validation failures as an <see cref="ApiProblem"/>, like
/// every other error: the problems decide the body, the status is the framework's.
/// </summary>
public sealed class ApiProblemWriter : IApiErrorWriter
{
    public IResult InvalidRequest(IReadOnlyList<ResultProblem> problems, int status, ApiOperation? operation) =>
        new ProblemResult(problems, status);

    private sealed class ProblemResult(IReadOnlyList<ResultProblem> problems, int status) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            var problem = ApiProblems.From(problems, ApiProblems.TraceId(httpContext)) with { Status = status };
            return ApiProblems.WriteAsync(httpContext, problem);
        }
    }
}
