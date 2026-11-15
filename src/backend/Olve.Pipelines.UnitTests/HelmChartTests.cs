using Olve.Pipelines.Recorder.Recordings;

namespace Olve.Pipelines.UnitTests;

/// <summary>
/// The server is the only writer of its state and the only controller of its Jobs. A rolling update overlaps the old
/// and new pod during a self-deploy: the new one loads state, reattaches to running Jobs and fails an attempt whose
/// result the old one already collected ("the step exited 0 but uploaded no result.tar.gz").
/// </summary>
public class HelmChartTests
{
    private static readonly string Root = RecordingStore.FindRepoRoot(AppContext.BaseDirectory)!;

    [Test]
    public async Task The_server_is_replaced_not_rolled_and_runs_once()
    {
        var deployment = await File.ReadAllTextAsync(Path.Combine(Root, "src/deploy/helm/templates/deployment.yaml"));
        var values = await File.ReadAllTextAsync(Path.Combine(Root, "src/deploy/helm/values.yaml"));

        await Assert.That(deployment).Contains("  strategy:\n    type: Recreate\n");
        await Assert.That(values).Contains("replicaCount: 1\n");
    }
}
