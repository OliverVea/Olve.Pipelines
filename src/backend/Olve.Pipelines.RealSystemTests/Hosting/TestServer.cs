using System.Diagnostics;
using System.Text.Json.Nodes;
using Olve.Pipelines.Api;

namespace Olve.Pipelines.RealSystemTests.Hosting;

/// <summary>A pipeline a test owns: bound, polling off, its fixture reconciled.</summary>
public sealed record TestPipeline(Guid Id, string Name, string Repo);

/// <summary>
/// The host a test runs against, for the length of the test: <c>await using var server = await TestServer.LeaseAsync();</c>.
/// Which host, and how many exist, stays in here. Tests never clean up: they name everything with
/// <see cref="NewName"/> and assert only on what they own.
/// </summary>
public sealed class TestServer : IAsyncDisposable
{
    /// <summary>What everything tests create is named with; the sweeper removes what is old.</summary>
    public const string NamePrefix = "e2e-";

    private const int SharedLeasesPerHost = 16;

    private static readonly Lazy<IReadOnlyList<Host>> Hosts = new(() => TestTargets.Load().Select(t => new Host(t)).ToList());

    private static readonly Lazy<ServerPool<Host>> Pool = new(() => new ServerPool<Host>(Hosts.Value, h => h.ResetAsync(), SharedLeasesPerHost));

    private readonly ServerPool<Host>.Lease _lease;

    private TestServer(ServerPool<Host>.Lease lease) => _lease = lease;

    private Host Current => _lease.Host;

    public PipelinesApi Api => Current.Api;
    public TestCluster Cluster => Current.Cluster;
    public TestBucket Bucket => Current.Bucket;
    public GitHubStandIn GitHub => Current.GitHub;
    public TestTarget Target => Current.Target;

    public static async Task<TestServer> LeaseAsync()
    {
        object? fresh = null;
        var lease = TestContext.Current?.StateBag.Items.TryGetValue(ExclusiveServerAttribute.Key, out fresh) == true
            ? await Pool.Value.AcquireExclusiveAsync(fresh is true)
            : await Pool.Value.AcquireSharedAsync();
        try
        {
            await lease.Host.EnsureGuardedAsync();
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
        return new TestServer(lease);
    }

    /// <summary>Every host of the pool, unleased: for host-wide reports and the sweeper, not for tests.</summary>
    internal static async Task<IReadOnlyList<Host>> GuardedHostsAsync()
    {
        foreach (var host in Hosts.Value)
            await host.EnsureGuardedAsync();
        return Hosts.Value;
    }

    public static string NewName(string hint = "t") =>
        $"{NamePrefix}{hint}-{Guid.NewGuid():N}"[..Math.Min(NamePrefix.Length + hint.Length + 13, 63)];

    /// <summary>A new pipeline holding the fixture, with its first version active.</summary>
    public async Task<TestPipeline> NewPipelineAsync(string hint = "t")
    {
        var pipeline = await BindAsync(hint);
        var reconcile = await Api.ReconcileAsync(pipeline.Id);
        if (reconcile.Detail is not null)
            throw new InvalidOperationException($"The fixture did not reconcile: {reconcile.Detail}");
        return pipeline;
    }

    /// <summary>A new pipeline, bound but not reconciled, with polling off so the test decides when it syncs.</summary>
    public async Task<TestPipeline> BindAsync(string hint = "t")
    {
        var name = NewName(hint);
        var repo = $"e2e/{name}";
        var binding = await Api.CreatePipelineAsync(repo);
        await Api.SetPollingAsync(binding.PipelineId, polls: false);
        return new TestPipeline(binding.PipelineId, name, repo);
    }

    public Task<Run> StartRunAsync(TestPipeline pipeline, JsonObject? input = null) =>
        Api.StartRunAsync(pipeline.Id, input ?? []);

    public Task<Run> WaitForRunAsync(TestPipeline pipeline, int number) =>
        Wait.UntilAsync(() => Api.GetRunAsync(pipeline.Id, number), r => r.Status != RunStatus.Running, Wait.Run,
            $"run #{number} of {pipeline.Name} to finish");

    /// <summary>
    /// The chaos tier's fault seam, <c>host.sh crash</c>. Answers once armed, so start what it waits for after
    /// this; the task it answers with completes when the server is ready again. Needs <see cref="ExclusiveServerAttribute"/>.
    /// </summary>
    public Task<Task> ArmServerCrashAsync(string afterLogLine, TimeSpan delay, TimeSpan downtime, bool graceful = false)
    {
        string[] options = ["--after", afterLogLine, "--delay-ms", $"{delay.TotalMilliseconds:0}", "--down-ms", $"{downtime.TotalMilliseconds:0}"];
        return Current.ArmHostCommandAsync(Target.CrashCommand, graceful ? [.. options, "--graceful"] : options);
    }

    public ValueTask DisposeAsync() => _lease.DisposeAsync();

    /// <summary>One target with its clients. Nothing reaches a target before it passed the guard.</summary>
    internal sealed class Host
    {
        private readonly Lazy<Task> _guard;

        public Host(TestTarget target)
        {
            Target = target;
            Api = new PipelinesApi(new HttpClient { BaseAddress = new Uri(target.ApiUrl), Timeout = TimeSpan.FromMinutes(5) });
            GitHub = new GitHubStandIn(new HttpClient { BaseAddress = new Uri(target.GitHubUrl) });
            Cluster = new TestCluster(target.Kubernetes);
            Bucket = new TestBucket(target.Storage);
            _guard = new Lazy<Task>(GuardAsync);
        }

        public override string ToString() => Target.ApiUrl;

        public TestTarget Target { get; }
        public PipelinesApi Api { get; }
        public GitHubStandIn GitHub { get; }
        public TestCluster Cluster { get; }
        public TestBucket Bucket { get; }

        public Task EnsureGuardedAsync() => _guard.Value;

        private async Task GuardAsync()
        {
            var kubernetes = new Uri(Target.Kubernetes.Server);
            if (TargetGuard.RefuseDescriptor(new Uri(Target.ApiUrl), kubernetes, Target.ClusterId) is { } refused)
                throw new InvalidOperationException(refused);
            if (TargetGuard.RefuseCluster(kubernetes, Target.ClusterId, await Cluster.ReadMarkerAsync()) is { } refusedCluster)
                throw new InvalidOperationException(refusedCluster);

            // The server's own cluster must be the marked one, not merely the descriptor's.
            var binding = await Api.CreatePipelineAsync($"e2e/{NewName("guard")}");
            await Api.SetSecretAsync(binding.PipelineId, "GUARD", "guard");
            var seen = await Cluster.Client.GetSecretAsync(Cluster.Namespace, TestCluster.PipelineSecretName(binding.PipelineId));
            if (seen is null)
                throw new InvalidOperationException($"Refusing {Target.ApiUrl}: it wrote its secret somewhere other than the test cluster at {kubernetes}.");
            await Api.SetPollingAsync(binding.PipelineId, polls: false);
        }

        public async Task ResetAsync()
        {
            using var process = StartHostCommand(Target.ResetCommand, []);
            await FinishAsync(process, process.StandardOutput.ReadToEndAsync());
        }

        /// <summary>Starts a command that prints one line when armed; answers with the rest of its life.</summary>
        public async Task<Task> ArmHostCommandAsync(string? command, string[] arguments)
        {
            var process = StartHostCommand(command, arguments);
            if (await process.StandardOutput.ReadLineAsync() is null)
                await FinishAsync(process, Task.CompletedTask);
            return FinishAndDisposeAsync(process);
        }

        private static async Task FinishAndDisposeAsync(Process process)
        {
            using (process)
                await FinishAsync(process, process.StandardOutput.ReadToEndAsync());
        }

        private static Process StartHostCommand(string? command, string[] arguments)
        {
            if (string.IsNullOrWhiteSpace(command))
                throw new InvalidOperationException("the host's descriptor lacks this command: start the host again with host.sh up");

            return Process.Start(new ProcessStartInfo("/bin/sh", ["-c", $"{command} \"$@\"", "sh", .. arguments])
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            }) ?? throw new InvalidOperationException($"'{command}' did not start");
        }

        private static async Task FinishAsync(Process process, Task output)
        {
            var error = process.StandardError.ReadToEndAsync();
            await output;
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"'{process.StartInfo.ArgumentList[1]}' exited {process.ExitCode}: {await error}");
        }
    }
}
