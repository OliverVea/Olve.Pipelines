using Olve.Pipelines.Jobs;

namespace Olve.Pipelines.Shared.Persistence;

public record JobSnapshot(Job[] Jobs, JobGroup[] JobGroups);
