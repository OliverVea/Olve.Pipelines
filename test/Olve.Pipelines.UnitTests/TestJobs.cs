using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Utilities.Ids;
using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.UnitTests;

/// <summary>Builders for the placeholder <see cref="Job"/> used across the job machinery tests.</summary>
internal static class TestJobs
{
    public static Job Make(
        Id<Pipeline>? pipelineId = null,
        string stepName = "build",
        DateTimeOffset? createdAt = null,
        JobStatus? status = null,
        Id<Job>? id = null)
        => new(
            id ?? Id.New<Job>(),
            pipelineId ?? Id.New<Pipeline>(),
            stepName,
            Image: "alpine:latest",
            Script: "echo hi",
            CreatedAt: createdAt ?? DateTimeOffset.UtcNow,
            Status: status ?? new Scheduled());
}
