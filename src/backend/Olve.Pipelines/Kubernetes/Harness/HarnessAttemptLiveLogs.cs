using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines.Runs;

namespace Olve.Pipelines.Kubernetes.Harness;

/// <summary>Live logs of a running step attempt, read from its harness pod (step container, else olve-init).</summary>
public sealed class HarnessAttemptLiveLogs(HarnessJobControl control, ILogger<HarnessAttemptLiveLogs> logger) : IAttemptLiveLogs
{
    public async Task<string?> TryGetAsync(Attempt attempt, CancellationToken ct)
    {
        HarnessLogs logs;
        try
        {
            logs = await control.GetLogsAsync(attempt.KubernetesJobName, HarnessJobBuilder.StepContainerName, hasInit: true, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Could not read live logs of K8s Job {JobName} (step '{StepName}', run #{RunNumber})",
                attempt.KubernetesJobName, attempt.StepName, attempt.RunNumber);
            return null;
        }

        if (string.IsNullOrEmpty(logs.Main) && string.IsNullOrWhiteSpace(logs.Init))
            return null;
        return string.IsNullOrEmpty(logs.Main)
            ? $"--- {HarnessJobBuilder.InitContainerName} ---\n{logs.Init}"
            : logs.Main;
    }
}
