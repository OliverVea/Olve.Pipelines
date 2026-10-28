namespace Olve.Pipelines.Kubernetes;

public record KubernetesJobStatus(string JobName, JobPhase Phase, string? Message = null);

public enum JobPhase
{
    Pending,
    Running,
    Succeeded,
    Failed,
}
