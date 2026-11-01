namespace Olve.Pipelines.RealSystemTests.Hosting;

/// <summary>Records how full each host is, so a scaling bug on an aged host doesn't pass for a flake.</summary>
public static class HostReport
{
    [Before(HookType.TestSession)]
    public static Task BeforeRun() => WriteAsync("before");

    [After(HookType.TestSession)]
    public static Task AfterRun() => WriteAsync("after");

    private static async Task WriteAsync(string when)
    {
        foreach (var host in await TestServer.GuardedHostsAsync())
        {
            var pipelines = await host.Api.ListPipelinesAsync();
            var jobs = await host.Cluster.Client.ListJobsAsync(host.Cluster.Namespace);
            var secrets = await host.Cluster.Client.ListSecretsAsync(host.Cluster.Namespace);
            var objects = await host.Bucket.ListAsync("");
            Console.WriteLine(
                $"host {host.Target} {when} the run: {pipelines.Count} pipelines, {jobs.Count} Jobs, {secrets.Count} Secrets, " +
                $"{objects.Count} objects, {objects.Sum(o => o.Size) / (1024 * 1024)} MiB");
        }
    }
}
