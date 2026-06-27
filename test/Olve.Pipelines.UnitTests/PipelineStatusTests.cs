using Olve.Pipelines.Pipelines;

namespace Olve.Pipelines.UnitTests;

public class PipelineStatusTests
{
    private static StepHealth Step(string status) => new("step", "production", status, null);

    [Test]
    public async Task NoSteps_IsIdle()
        => await Assert.That(PipelineStatus.Compute([])).IsEqualTo(PipelineStatus.Idle);

    [Test]
    public async Task AllDone_IsHealthy()
        => await Assert.That(PipelineStatus.Compute([Step("done"), Step("done")]))
            .IsEqualTo(PipelineStatus.Healthy);

    [Test]
    public async Task RunningWithNoFailures_IsRunningHealthy()
        => await Assert.That(PipelineStatus.Compute([Step("done"), Step("in-progress")]))
            .IsEqualTo(PipelineStatus.RunningHealthy);

    [Test]
    public async Task ScheduledCountsAsRunning()
        => await Assert.That(PipelineStatus.Compute([Step("done"), Step("scheduled")]))
            .IsEqualTo(PipelineStatus.RunningHealthy);

    [Test]
    public async Task RunningWithAFailure_IsRunningUnhealthy()
        => await Assert.That(PipelineStatus.Compute([Step("in-progress"), Step("failed")]))
            .IsEqualTo(PipelineStatus.RunningUnhealthy);

    [Test]
    public async Task NotRunningWithAFailure_IsUnhealthy()
        => await Assert.That(PipelineStatus.Compute([Step("done"), Step("failed")]))
            .IsEqualTo(PipelineStatus.Unhealthy);

    [Test]
    public async Task IdleSteps_AreIdle()
        => await Assert.That(PipelineStatus.Compute([Step("done"), Step("idle")]))
            .IsEqualTo(PipelineStatus.Idle);

    [Test]
    public async Task CancelledOrObsolete_WithoutFailure_IsIdle()
        => await Assert.That(PipelineStatus.Compute([Step("done"), Step("cancelled"), Step("obsolete")]))
            .IsEqualTo(PipelineStatus.Idle);
}
