using System.Net;
using System.Text.Json.Nodes;
using Olve.Pipelines.Api;

namespace Olve.Pipelines.RealSystemTests.Flows;

/// <summary>A matrix step on a real cluster: one Job per cell, files across cells, and a cascade.</summary>
public class MatrixFlowTests
{
    private static readonly string[] Cells = ["paint[shape=square,size=s]", "paint[shape=square,size=l]", "paint[shape=circle,size=s]"];

    private static async Task<TestPipeline> MatrixPipelineAsync(TestServer server)
    {
        var pipeline = await server.BindAsync("matrix");
        var fixture = await File.ReadAllTextAsync(Path.Combine(TestTargets.RepoRoot(), "tools/real-system/matrix-fixture/pipeline.ts"));
        await server.GitHub.CommitAsync(pipeline.Repo, new Dictionary<string, string>
        {
            [".pipelines/pipeline.ts"] = fixture.Replace("__PIPELINE_NAME__", pipeline.Name),
        });
        await Assert.That(await server.Api.ReconcileAsync(pipeline.Id)).IsEqualTo(new ApiReply(HttpStatusCode.OK, null));
        return pipeline;
    }

    private static RunStep Step(Run run, string name) => run.Stages.SelectMany(s => s.Steps).Single(s => s.Name == name);

    [Test]
    public async Task EveryCellRuns_AndALaterStepReadsTheirOutputsAndFiles()
    {
        await using var server = await TestServer.LeaseAsync();
        var pipeline = await MatrixPipelineAsync(server);

        var run = await server.StartRunAsync(pipeline);
        var finished = await server.WaitForRunAsync(pipeline, run.Number);

        await Assert.That(finished.Status).IsEqualTo(RunStatus.Succeeded);
        var paint = Step(finished, "paint");
        await Assert.That(paint.Status).IsEqualTo(RunStepStatus.Succeeded);
        await Assert.That(paint.Cells!.Select(c => (c.Name, c.Status))).IsEquivalentTo(Cells.Select(c => (c, RunStepStatus.Succeeded)));

        var gathered = JsonNode.Parse(await server.Api.GetStepOutputAsync(pipeline.Id, run.Number, "gather"))!["output"]!;
        await Assert.That(gathered["labels"]!.AsArray().Select(l => l!.GetValue<string>()))
            .IsEquivalentTo(["square-s=square-s", "square-l=square-l", "circle-s=circle-s"]);
        await Assert.That(gathered["texts"]!.AsArray().Select(t => t!.GetValue<string>()))
            .IsEquivalentTo(["painted square-s", "painted square-l", "painted circle-s"]);
        await Assert.That(await server.Api.GetStepFileAsync(pipeline.Id, run.Number, Cells[1], "cell.txt")).IsEqualTo("painted square-l");
    }

    [Test]
    public async Task AFailedCell_CancelsTheOthers_NamingTheFailure()
    {
        await using var server = await TestServer.LeaseAsync();
        var pipeline = await MatrixPipelineAsync(server);

        var run = await server.StartRunAsync(pipeline, new JsonObject { ["failCell"] = "square-l" });
        var finished = await server.WaitForRunAsync(pipeline, run.Number);

        await Assert.That(finished.Status).IsEqualTo(RunStatus.Failed);
        var paint = Step(finished, "paint");
        await Assert.That(paint.Status).IsEqualTo(RunStepStatus.Failed);
        await Assert.That(paint.Reason).StartsWith("square · l failed: ");
        await Assert.That(paint.Cells!.Select(c => (c.Name, c.Status, c.Reason))).IsEquivalentTo([
            (Cells[0], RunStepStatus.Cancelled, "cancelled: square · l failed"),
            (Cells[1], RunStepStatus.Failed, paint.Cells![1].Reason),
            (Cells[2], RunStepStatus.Cancelled, "cancelled: square · l failed"),
        ]);
        await Assert.That(paint.Cells![1].Reason).Contains("cell square-l was asked to fail");
    }
}
