using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.Pipelines.Runs;

/// <summary>An unfinished attempt, addressed <c>&lt;pipeline&gt;#&lt;run&gt;/&lt;step&gt;@&lt;attempt&gt;</c>.</summary>
/// <param name="Status">The attempt-status discriminator (<c>waiting</c> / <c>scheduled</c> / <c>in-progress</c>).</param>
/// <param name="StartedAt">When its K8s Job started (null while scheduled).</param>
public record ActiveAttempt(
    Id<Pipeline> PipelineId,
    string PipelineName,
    int Run,
    string Step,
    int Attempt,
    Contract.AttemptState Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt);

/// <summary>Every active attempt across pipelines ("what's running"), oldest first. Stateless query; transient.</summary>
public class ActiveAttemptQuery(AttemptService attempts, PipelineService pipelines)
{
    /// <param name="waitingOnly">Only attempts that wait for external input.</param>
    public IReadOnlyList<ActiveAttempt> List(bool waitingOnly = false)
        => attempts.List()
            .Where(a => waitingOnly ? a.Status is AttemptStatus.Waiting : a.Status.IsActive())
            .OrderBy(a => a.CreatedAt)
            .Select(a => new ActiveAttempt(
                a.PipelineId,
                pipelines.TryGet(a.PipelineId, out var p) ? p.Name : a.PipelineId.ToString(),
                a.RunNumber, a.StepName, a.Number, a.Status.State(), a.CreatedAt, a.StartedAt()))
            .ToList();
}
