
namespace Olve.Pipelines.RealSystemTests.Hosting;

/// <summary>What a sweep removed.</summary>
public sealed record SweepReport(IReadOnlyList<string> Pipelines, IReadOnlyList<string> Secrets, IReadOnlyList<string> Jobs, IReadOnlyList<string> Objects);

/// <summary>
/// The fallback behind retention: removes what tests left, by name prefix and age. Pipelines go through the
/// API, the Secrets, Jobs and objects contract tests create go directly. Never touches an unprefixed name.
/// </summary>
public sealed class Sweeper(PipelinesApi api, TestCluster cluster, TestBucket bucket, TimeProvider time)
{
    /// <summary>Longer than any test, so a sweep can't pull a resource from under a running test.</summary>
    public static readonly TimeSpan DefaultAge = TimeSpan.FromHours(2);

    public async Task<SweepReport> SweepAsync(string prefix, TimeSpan olderThan)
    {
        if (!prefix.StartsWith(TestServer.NamePrefix, StringComparison.Ordinal))
            throw new ArgumentException($"The sweeper only removes names starting with '{TestServer.NamePrefix}', not '{prefix}'.", nameof(prefix));

        var cutoff = time.GetUtcNow() - olderThan;
        bool Mine(string name) => name.StartsWith(prefix, StringComparison.Ordinal);

        var pipelines = new List<string>();
        foreach (var pipeline in await api.ListPipelinesAsync())
        {
            var repoName = pipeline.Repo?.Split('/')[^1] ?? "";
            if (!Mine(repoName))
                continue;
            var binding = await api.GetBindingAsync(pipeline.Id);
            if (binding.CreatedAt > cutoff)
                continue;
            await api.DeletePipelineAsync(pipeline.Id);
            pipelines.Add(repoName);
        }

        var secrets = (await cluster.Client.ListSecretsAsync(cluster.Namespace))
            .Where(s => Mine(s.Name) && s.Created <= cutoff).Select(s => s.Name).ToList();
        foreach (var secret in secrets)
            await cluster.Client.DeleteSecretAsync(cluster.Namespace, secret);

        var jobs = (await cluster.Client.ListJobsAsync(cluster.Namespace))
            .Where(j => Mine(j.Name) && j.Created <= cutoff).Select(j => j.Name).ToList();
        foreach (var job in jobs)
            await cluster.Client.DeleteJobAsync(cluster.Namespace, job);

        var objects = (await bucket.ListAsync(prefix)).Where(o => o.LastModified <= cutoff).Select(o => o.Key).ToList();
        if (objects.Count > 0)
            await bucket.Objects.DeleteAsync(objects, CancellationToken.None);

        return new SweepReport(pipelines, secrets, jobs, objects);
    }
}

/// <summary>The sweep as a command: <c>tools/real-system/host.sh sweep</c>.</summary>
public class SweepCommand
{
    [Test, Explicit]
    public async Task Sweep()
    {
        foreach (var host in await TestServer.GuardedHostsAsync())
        {
            var report = await new Sweeper(host.Api, host.Cluster, host.Bucket, TimeProvider.System)
                .SweepAsync(TestServer.NamePrefix, Sweeper.DefaultAge);
            Console.WriteLine(
                $"swept {host.Target}: {report.Pipelines.Count} pipelines, {report.Secrets.Count} Secrets, {report.Jobs.Count} Jobs, {report.Objects.Count} objects");
        }
    }
}
