using Olve.MinimalApi;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Jobs;

public static class JobEndpoints
{
    public static void MapJobEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/jobs");

        group.MapGet("/", Result<Job[]> (JobService service)
                => service.ListJobs().ToArray())
            .WithResultMapping<Job[]>()
            .WithName("ListJobs")
            .AllowAnonymous();

        group.MapGet("/{id}", Result<Job> (JobService service, Id<Job> id)
                => service.TryGetJob<Job>(id, out var job)
                    ? Result.Success(job)
                    : Result.Failure<Job>(new ResultProblem($"Job '{id}' not found.")))
            .WithResultMapping<Job>()
            .WithName("GetJob")
            .AllowAnonymous();

        group.MapGet("/{id}/logs", async Task<Result<string>> (JobLogService service, Id<Job> id, CancellationToken ct)
                => await service.GetLogsAsync(id, ct))
            .WithResultMapping<string>()
            .WithName("GetJobLogs")
            .AllowAnonymous();

        group.MapGet("/queue", Result<Id<Job>[]> (JobQueueService service)
                => service.GetQueuedJobIds().ToArray())
            .WithResultMapping<Id<Job>[]>()
            .WithName("GetJobQueue")
            .AllowAnonymous();

        group.MapPost("/{id}/cancel", Result (JobService service, Id<Job> id)
                => service.CancelJob(id))
            .WithResultMapping()
            .WithName("CancelJob");

        group.MapDelete("/{id}", DeletionResult (JobService service, Id<Job> id)
                => service.DeleteJob(id))
            .WithDeletionMapping()
            .WithName("DeleteJob");
    }
}
