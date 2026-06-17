using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Shared;
using Olve.Results;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests;

/// <summary>
/// Covers the production-trigger gate that <c>POST /trigger/production</c> consults
/// (<see cref="ProductionStepService.HasConfiguredSteps"/>). These rules used to be asserted via
/// the integration suite by creating steps over the API; the API no longer writes shape, so the
/// rule is verified directly here.
/// </summary>
public class ProductionStepServiceTests
{
    private static ProductionStepService NewService()
    {
        var store = new EntityStore<ProductionStep>([]);
        var config = new AttachmentStore<ProductionStep, StepConfiguration>(store);
        return new ProductionStepService(store, config, new IdProvider());
    }

    private static T Pick<T>(Result<T> result)
    {
        if (result.TryPickProblems(out var problems, out var value))
            throw new InvalidOperationException(string.Join("; ", problems.Select(p => p.Message)));
        return value;
    }

    [Test]
    public async Task HasConfiguredSteps_NoSteps_ReturnsFalse()
    {
        var service = NewService();
        var pipelineId = Id.New<Pipeline>();

        await Assert.That(service.HasConfiguredSteps(pipelineId)).IsFalse();
    }

    [Test]
    public async Task HasConfiguredSteps_StepWithoutConfiguration_ReturnsFalse()
    {
        var service = NewService();
        var pipelineId = Id.New<Pipeline>();

        Pick(service.Create(pipelineId, "build"));

        await Assert.That(service.HasConfiguredSteps(pipelineId)).IsFalse();
    }

    [Test]
    public async Task HasConfiguredSteps_AllStepsConfigured_ReturnsTrue()
    {
        var service = NewService();
        var pipelineId = Id.New<Pipeline>();

        var step = Pick(service.Create(pipelineId, "build"));
        service.SetConfiguration(step.Id, new StepConfiguration("alpine:latest", "echo hi", null));

        await Assert.That(service.HasConfiguredSteps(pipelineId)).IsTrue();
    }
}
