using Olve.MinimalApi;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Utilities.Paginations;

namespace Olve.Pipelines.Jobs;

/// <summary>
/// The job queue's view of attempts (each attempt runs as one K8s Job). Interim surface kept for
/// the CLI/frontend; the run-scoped attempts API is B8. Attempts are history, so there is no delete.
/// </summary>
public static class JobEndpoints
{
    public static void MapJobEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/jobs");

        group.MapGet("/", Result<Page<Attempt>> (AttemptService service, [AsParameters] ListAttemptsRequest request)
                => service.List(request))
            .WithResultMapping<Page<Attempt>>()
            .WithName("ListJobs")
            .AllowAnonymous();

        group.MapGet("/{id}", Result<Attempt> (AttemptService service, Id<Attempt> id)
                => service.TryGet(id, out var attempt)
                    ? attempt
                    : new ResultProblem("Attempt '{0}' not found.", id))
            .WithResultMapping<Attempt>()
            .WithName("GetJob")
            .AllowAnonymous();

        group.MapGet("/{id}/logs", async Task<Result<string>> (JobLogService service, Id<Attempt> id, CancellationToken ct)
                => await service.GetLogsAsync(id, ct))
            .WithResultMapping<string>()
            .WithName("GetJobLogs")
            .AllowAnonymous();

        group.MapGet("/queue", Result<Id<Attempt>[]> (JobQueueService service)
                => service.GetQueuedAttemptIds().ToArray())
            .WithResultMapping<Id<Attempt>[]>()
            .WithName("GetJobQueue")
            .AllowAnonymous();

        group.MapPost("/{id}/cancel", Result (AttemptService service, Id<Attempt> id)
                => service.Cancel(id))
            .WithResultMapping()
            .WithName("CancelJob");
    }
}
