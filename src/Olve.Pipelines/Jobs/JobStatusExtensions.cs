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
}
