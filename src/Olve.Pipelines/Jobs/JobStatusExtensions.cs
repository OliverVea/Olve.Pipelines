using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.Jobs;

public static class JobStatusExtensions
{
    public static bool IsTerminal(this JobStatus status) => status is Done or Failed or Cancelled or Obsolete;

    /// <summary>
    /// The status discriminator used on the wire and by the frontend
    /// (scheduled / in-progress / done / failed / cancelled / obsolete).
    /// </summary>
    public static string Discriminator(this JobStatus status) => status switch
    {
        Scheduled => "scheduled",
        InProgress => "in-progress",
        Done => "done",
        Failed => "failed",
        Cancelled => "cancelled",
        Obsolete => "obsolete",
        _ => "idle",
    };

    /// <summary>
    /// When this job last changed status. Each status variant carries the timestamp of the
    /// transition into it; <see cref="Scheduled"/> and <see cref="Obsolete"/> carry none, so we
    /// fall back to <see cref="Job.CreatedAt"/> (the moment the job was scheduled).
    /// </summary>
    public static DateTimeOffset LastChangedAt(this Job job) => job.Status switch
    {
        InProgress s => s.StartedAt,
        Done s => s.CompletedAt,
        Failed s => s.FailedAt,
        Cancelled s => s.CancelledAt,
        _ => job.CreatedAt,
    };
}
