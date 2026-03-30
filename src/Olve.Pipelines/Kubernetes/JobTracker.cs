using System.Collections.Concurrent;

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

    public IReadOnlyList<JobRecord> GetByPipelineId(Guid pipelineId)
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
    Guid PipelineId,
    JobRecordPhase Phase,
    Guid? SourceBundleId,
    Guid? ArtifactBundleId,
    DateTimeOffset CreatedAt);

public enum JobRecordPhase
{
    Sourcing,
    Building,
    Processing,
}
