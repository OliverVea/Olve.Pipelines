using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines.Building;
using static Olve.Pipelines.Jobs.Job;

namespace Olve.Pipelines.Jobs;

public class JobLogService(
    IAmazonS3 s3,
    StorageOptions storageOptions,
    JobService jobService,
    JobGroupService jobGroupService)
{
    public async Task<Result<string>> GetLogsAsync(Id<Job> jobId, CancellationToken ct)
    {
        if (!jobService.TryGetJob<Job>(jobId, out var job))
            return new ResultProblem("Job '{0}' not found.", jobId);

        if (!TryGetArtifactBundleId(job, out var bundleId))
            return new ResultProblem("Job '{0}' has no associated artifact bundle.", jobId);

        var key = KubernetesJobExecutor.LogS3Key(job.PipelineId, bundleId, jobId);

        try
        {
            using var response = await s3.GetObjectAsync(new GetObjectRequest
            {
                BucketName = storageOptions.Bucket,
                Key = key,
            }, ct);

            using var reader = new StreamReader(response.ResponseStream);
            var text = await reader.ReadToEndAsync(ct);
            return text;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new ResultProblem("Logs not available for job '{0}'.", jobId);
        }
    }

    private bool TryGetArtifactBundleId(Job job, out Id<ArtifactBundle> bundleId)
    {
        switch (job)
        {
            case ProcessingJob pj:
                bundleId = pj.ArtifactBundleId;
                return true;
            case ProductionJob prj:
                if (jobGroupService.TryGet(prj.JobGroupId, out var group) && group is ProductionJobGroup pg)
                {
                    bundleId = pg.ArtifactBundleId;
                    return true;
                }
                break;
        }

        bundleId = default;
        return false;
    }
}
