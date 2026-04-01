using Microsoft.AspNetCore.Http;
using Olve.Results;

namespace Olve.Pipelines.Shared;

public static class DeletionMappingExtensions
{
    public static RouteHandlerBuilder WithDeletionMapping(this RouteHandlerBuilder builder)
    {
        builder.AddEndpointFilter(async (context, next) =>
        {
            var result = await next(context);

            return result switch
            {
                DeletionResult { Succeeded: true } => TypedResults.Ok(),
                DeletionResult { WasNotFound: true } => TypedResults.NotFound(),
                DeletionResult deletionResult => TypedResults.BadRequest(deletionResult.Problems),
                _ => result,
            };
        });

        builder.Produces(200)
            .Produces(404)
            .Produces<ResultProblemCollection>(400);

        return builder;
    }
}
