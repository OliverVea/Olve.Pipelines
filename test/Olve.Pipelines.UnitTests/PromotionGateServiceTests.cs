using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Shared;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests;

public class PromotionGateServiceTests
{
    private static (PromotionGateService Gate, EntityStore<ProcessingStep> Steps) Create()
    {
        var steps = new EntityStore<ProcessingStep>([]);
        var store = new AttachmentStore<ProcessingStep, ProcessingStepPromotion>(steps);
        return (new PromotionGateService(store), steps);
    }

    [Test]
    public async Task AbsentState_IsNotBlocked()
    {
        var (gate, _) = Create();
        await Assert.That(gate.IsBlocked(Id.New<ProcessingStep>())).IsFalse();
    }

    [Test]
    public async Task Block_ThenIsBlocked()
    {
        var (gate, _) = Create();
        var stepId = Id.New<ProcessingStep>();

        gate.Block(stepId);

        await Assert.That(gate.IsBlocked(stepId)).IsTrue();
    }

    [Test]
    public async Task Unblock_ClearsState()
    {
        var (gate, _) = Create();
        var stepId = Id.New<ProcessingStep>();

        gate.Block(stepId);
        gate.Unblock(stepId);

        await Assert.That(gate.IsBlocked(stepId)).IsFalse();
    }

    [Test]
    public async Task Set_TogglesBothWays()
    {
        var (gate, _) = Create();
        var stepId = Id.New<ProcessingStep>();

        gate.Set(stepId, true);
        await Assert.That(gate.IsBlocked(stepId)).IsTrue();

        gate.Set(stepId, false);
        await Assert.That(gate.IsBlocked(stepId)).IsFalse();
    }

    [Test]
    public async Task StepDeleted_AutoCleansPromotionState()
    {
        var (gate, steps) = Create();
        var step = new ProcessingStep(Id.New<ProcessingStep>(), "deploy", Id.New<Pipeline>(), 0);
        steps.Set(step);
        gate.Block(step.Id);

        steps.Delete(step.Id);

        // After the parent step is deleted, the attachment cascade clears the gate so a recreated
        // step with the same Id would not inherit a stale block.
        await Assert.That(gate.IsBlocked(step.Id)).IsFalse();
    }
}
