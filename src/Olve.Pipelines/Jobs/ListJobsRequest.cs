using Olve.Pipelines.Pipelines;

namespace Olve.Pipelines.Jobs;

public enum JobSortField
{
    CreatedAtDesc,
    CreatedAtAsc,
}

public record ListJobsRequest(
    Id<Pipeline>? PipelineId = null,
    int Page = 0,
    int PageSize = 50,
    JobSortField Sort = JobSortField.CreatedAtDesc);
