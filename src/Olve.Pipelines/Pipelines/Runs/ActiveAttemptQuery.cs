namespace Olve.Pipelines.Pipelines.Runs;

/// <summary>A scheduled or in-progress attempt, addressed <c>&lt;pipeline&gt;#&lt;run&gt;/&lt;step&gt;@&lt;attempt&gt;</c>.</summary>
/// <param name="Status">The attempt-status discriminator (<c>scheduled</c> / <c>in-progress</c>).</param>
/// <param name="StartedAt">When its K8s Job started (null while scheduled).</param>
public record ActiveAttempt(
    Id<Pipeline> PipelineId,
    string PipelineName,
    int Run,
    string Step,
    int Attempt,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt);

/// <summary>Every active attempt across pipelines ("what's running"), oldest first. Stateless query; transient.</summary>
public class ActiveAttemptQuery(AttemptService attempts, PipelineService pipelines)
{
    public IReadOnlyList<ActiveAttempt> List()
        => attempts.List()
            .Where(a => a.Status.IsActive())
            .OrderBy(a => a.CreatedAt)
            .Select(a => new ActiveAttempt(
                a.PipelineId,
                pipelines.TryGet(a.PipelineId, out var p) ? p.Name : a.PipelineId.ToString(),
                a.RunNumber, a.StepName, a.Number, a.Status.Discriminator(), a.CreatedAt, a.StartedAt()))
            .ToList();
}
