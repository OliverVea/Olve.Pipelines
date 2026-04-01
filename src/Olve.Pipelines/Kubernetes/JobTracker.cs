using System.Collections.Concurrent;
using Olve.Pipelines.Building;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Sourcing;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.Kubernetes;

public class JobTracker
{
    private readonly ConcurrentDictionary<string, JobRecord> _jobs = new();

    public void Track(JobRecord record)
    {
        _jobs[record.JobName] = record;
    }

    public IReadOnlyList<JobRecord> GetAll()
    {
        return _jobs.Values.ToArray();
    }

    public IReadOnlyList<JobRecord> GetByPipelineId(Id<Pipeline> pipelineId)
    {
        return _jobs.Values.Where(j => j.PipelineId == pipelineId).ToArray();
    }

    public bool TryGet(string jobName, out JobRecord? record)
    {
        return _jobs.TryGetValue(jobName, out record);
    }

    public void Remove(string jobName)
    {
        _jobs.TryRemove(jobName, out _);
    }
}

public record JobRecord(
    string JobName,
    Id<Pipeline> PipelineId,
    JobRecordPhase Phase,
    Id<SourceBundle>? SourceBundleId,
    Id<ArtifactBundle>? ArtifactBundleId,
    DateTimeOffset CreatedAt);

public enum JobRecordPhase
{
    Sourcing,
    Building,
    Processing,
}
