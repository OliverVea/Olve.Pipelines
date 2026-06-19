using Olve.Pipelines.FailureHandlers;
using Olve.Pipelines.Pipelines;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests;

public class FailureContextTests
{
    private static FailureContext Sample(Id<Pipeline> pipelineId) => new(
        pipelineId,
        PipelineName: "my-pipeline",
        FailedStepName: "build-and-package",
        FailedStepKind: FailedStepKind.Production,
        FailureReason: "exit code 1",
        LogKey: "bundles/abc/def/logs/xyz.log");

    [Test]
    public async Task ToEnvironmentVariables_EmitsAllWellKnownKeys()
    {
        var env = Sample(Id.New<Pipeline>()).ToEnvironmentVariables();

        await Assert.That(env.Keys).Contains(FailureContext.PipelineIdKey);
        await Assert.That(env.Keys).Contains(FailureContext.PipelineNameKey);
        await Assert.That(env.Keys).Contains(FailureContext.FailedStepKey);
        await Assert.That(env.Keys).Contains(FailureContext.FailedStepKindKey);
        await Assert.That(env.Keys).Contains(FailureContext.FailureReasonKey);
        await Assert.That(env.Keys).Contains(FailureContext.LogKeyKey);
    }

    [Test]
    public async Task ToEnvironmentVariables_UsesDashedGuidForPipelineId()
    {
        var pipelineId = Id.New<Pipeline>();
        var env = Sample(pipelineId).ToEnvironmentVariables();

        await Assert.That(env[FailureContext.PipelineIdKey]).IsEqualTo(pipelineId.Value.Value.ToString());
    }

    [Test]
    [Arguments(FailedStepKind.Production, "production")]
    [Arguments(FailedStepKind.Processing, "processing")]
    public async Task ToEnvironmentVariables_RendersStepKindLowercase(FailedStepKind kind, string expected)
    {
        var env = (Sample(Id.New<Pipeline>()) with { FailedStepKind = kind }).ToEnvironmentVariables();

        await Assert.That(env[FailureContext.FailedStepKindKey]).IsEqualTo(expected);
    }

    [Test]
    public async Task ToEnvironmentVariables_CarriesReasonAndLogKeyVerbatim()
    {
        var env = Sample(Id.New<Pipeline>()).ToEnvironmentVariables();

        await Assert.That(env[FailureContext.FailureReasonKey]).IsEqualTo("exit code 1");
        await Assert.That(env[FailureContext.LogKeyKey]).IsEqualTo("bundles/abc/def/logs/xyz.log");
    }
}
