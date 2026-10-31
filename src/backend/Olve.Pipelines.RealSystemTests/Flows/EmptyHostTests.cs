namespace Olve.Pipelines.RealSystemTests.Flows;

public class EmptyHostTests
{
    [Test, ExclusiveServer(Fresh = true)]
    public async Task AFreshHost_HasNoPipelines_AndStillTakesOne()
    {
        await using var server = await TestServer.LeaseAsync();

        var before = await server.Api.ListPipelinesAsync();
        var runs = await server.Bucket.ListAsync("runs/");
        var pipeline = await server.NewPipelineAsync("empty");

        // The guard's own pipeline can be there; a reset leaves nothing else.
        await Assert.That(before.Where(p => !p.Name.StartsWith($"{TestServer.NamePrefix}guard-", StringComparison.Ordinal))).IsEmpty();
        await Assert.That(runs).IsEmpty();
        await Assert.That((await server.Api.ListPipelinesAsync()).Select(p => p.Id)).Contains(pipeline.Id);
    }
}
