using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.Jobs;

public static class JobStatusExtensions
{
    public static bool IsTerminal(this JobStatus status) => status is Done or Failed or Cancelled or Obsolete;
}
