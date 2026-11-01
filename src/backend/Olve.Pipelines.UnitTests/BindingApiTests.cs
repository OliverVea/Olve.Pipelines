using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Configs;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Sync.ConfigSource;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Shared.Problems;
using Olve.Results;
using Olve.Utilities.Ids;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.UnitTests;

/// <summary>The binding operations, handler by handler, over the real services and the config sync.</summary>
public class BindingApiTests
{
    private sealed class FakeReconciler : IConfigReconciler
    {
        public TaskCompletionSource<string> Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Calls { get; } = [];
        public string? Failure { get; set; }

        public Task<Result<ReconcileReport>> ReconcileAsync(PipelineConfigBinding binding, string commitSha, CancellationToken ct)
        {
            lock (Calls) Calls.Add(commitSha);
            Called.TrySetResult(commitSha);
            var version = new PipelineVersion(Id.New<PipelineVersion>(), binding.PipelineId, 1, "{}", "sha256:x", commitSha, "k",
                new Dictionary<string, int>(), new Dictionary<string, int>(), DateTimeOffset.UnixEpoch);
            return Task.FromResult(Failure is { } failure
                ? Result.Failure<ReconcileReport>(new ResultProblem("{0}", failure))
                : new ReconcileReport(version, Created: true, [], [], []));
        }
    }

    private sealed class NoPush : IPushTriggerHandler
    {
        public Task<Result> FireAsync(PipelineConfigBinding binding, string headSha, CancellationToken ct, Olve.Pipelines.Pipelines.Runs.RunOrigin? origin = null)
            => Task.FromResult(Result.Success());
    }

    private sealed record Api(ServiceProvider Provider, FakeConfigSource Source, FakeReconciler Reconciler) : IAsyncDisposable
    {
        public T Get<T>() where T : notnull => Provider.GetRequiredService<T>();

        public PipelineConfigBindingService Bindings => Get<PipelineConfigBindingService>();
        public IReadOnlyList<Pipeline> Pipelines => Get<PipelineService>().List();

        public Task<Contract.BindingsCreateResponse> Create(string repo, string? branch = null, string? path = null, string? credentials = null) =>
            new CreatePipelineHandler(Get<PipelineService>(), Bindings).HandleAsync(
                new Contract.BindingsCreateRequest(new Contract.CreatePipeline { Repo = repo, Branch = branch, Path = path, CredentialsSecret = credentials }), default);

        public async Task<Contract.PipelineBinding> Bound(string repo = "acme/widgets") =>
            ((Contract.BindingsCreateResponse.Created)await Create(repo)).Body;

        public Task<Contract.BindingsGetResponse> GetBinding(Guid id) =>
            new GetBindingHandler(Bindings).HandleAsync(new Contract.BindingsGetRequest(id), default);

        public Task<Contract.BindingsStatusResponse> Status(Guid id) =>
            new GetBindingStatusHandler(Bindings, Get<PipelineActivationService>(), Get<PipelineVersionService>(), Get<ConfigActivationService>())
                .HandleAsync(new Contract.BindingsStatusRequest(id), default);

        public Task<Contract.BindingsUpdateResponse> Update(Guid id, string? credentials) =>
            new UpdateBindingHandler(Bindings).HandleAsync(new Contract.BindingsUpdateRequest(id, new Contract.UpdateBinding { CredentialsSecret = credentials }), default);

        public Task<Contract.BindingsSetBranchResponse> SetBranch(Guid id, string branch) =>
            new SetBindingBranchHandler(Bindings).HandleAsync(new Contract.BindingsSetBranchRequest(id, new Contract.SetBindingBranch { Branch = branch }), default);

        public Task<Contract.BindingsSetPollingResponse> SetPolling(Guid id, bool polls) =>
            new SetBindingPollingHandler(Bindings).HandleAsync(new Contract.BindingsSetPollingRequest(id, new Contract.SetBindingPolling { Polls = polls }), default);

        public Task<Contract.BindingsReconcileResponse> Reconcile(Guid id, bool? wait = true, bool? rebuild = null) =>
            new ReconcileBindingHandler(Get<DeployPollService>(), Bindings).HandleAsync(new Contract.BindingsReconcileRequest(id, wait, rebuild), default);

        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }

    private static Api Build()
    {
        var source = new FakeConfigSource();
        var reconciler = new FakeReconciler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddPipelineServices();
        services.AddSingleton<IConfigSource>(source);
        services.AddSingleton<IConfigReconciler>(reconciler);
        services.AddSingleton<IPushTriggerHandler>(new NoPush());
        return new Api(services.BuildServiceProvider(), source, reconciler);
    }

    private static readonly Guid Unknown = Guid.NewGuid();

    private static string NoBinding(Guid id) => $"Pipeline '{new Id<Pipeline>(new Id(id))}' has no config binding.";

    private static async Task AssertProblem(ApiProblem problem, int status, string detail)
    {
        await Assert.That(problem.Status).IsEqualTo(status);
        await Assert.That(problem.Detail).IsEqualTo(detail);
    }

    [Test]
    [Arguments("acme/widgets", null, null, "main", ".pipelines", "widgets")]
    [Arguments("acme/widgets", " ", "", "main", ".pipelines", "widgets")]
    [Arguments("acme/widgets", "release", "ci/pipelines", "release", "ci/pipelines", "widgets")]
    public async Task Create_BindsANewPipeline_WithDefaultsAndAProvisionalName(
        string repo, string? branch, string? path, string expectedBranch, string expectedPath, string expectedName)
    {
        await using var api = Build();

        var created = await api.Create(repo, branch, path, "GITHUB_TOKEN");

        var binding = ((Contract.BindingsCreateResponse.Created)created).Body;
        await Assert.That(binding.Repo).IsEqualTo(repo);
        await Assert.That(binding.Branch).IsEqualTo(expectedBranch);
        await Assert.That(binding.Path).IsEqualTo(expectedPath);
        await Assert.That(binding.CredentialsSecret).IsEqualTo("GITHUB_TOKEN");
        await Assert.That(api.Pipelines.Single().Name).IsEqualTo(expectedName);
        await Assert.That(api.Pipelines.Single().Id.Value.Value).IsEqualTo(binding.PipelineId);
    }

    [Test]
    public async Task Create_ThatCannotBind_LeavesNoPipeline()
    {
        await using var api = Build();

        var created = await api.Create(" ");

        await AssertProblem(((Contract.BindingsCreateResponse.BadRequest)created).Body, 400, "Binding requires a repository ('owner/name').");
        await Assert.That(api.Pipelines).IsEmpty();
    }

    [Test]
    public async Task Get_ReturnsTheBinding()
    {
        await using var api = Build();
        var bound = await api.Bound();

        var got = ((Contract.BindingsGetResponse.Ok)await api.GetBinding(bound.PipelineId)).Body;

        await Assert.That(got.Id).IsEqualTo(bound.Id);
        await Assert.That(got.Status.Result).IsEqualTo(Contract.ReconcileResult.NeverRun);
        await Assert.That(got.Polls).IsTrue();
    }

    [Test]
    public async Task UpdatingABinding_ChangesCredentialsBranchAndPolling()
    {
        await using var api = Build();
        var id = (await api.Bound()).PipelineId;

        var set = ((Contract.BindingsUpdateResponse.Ok)await api.Update(id, "GITHUB_TOKEN")).Body;
        await Assert.That(set.CredentialsSecret).IsEqualTo("GITHUB_TOKEN");
        var cleared = ((Contract.BindingsUpdateResponse.Ok)await api.Update(id, null)).Body;
        await Assert.That(cleared.CredentialsSecret).IsNull();

        var moved = ((Contract.BindingsSetBranchResponse.Ok)await api.SetBranch(id, "release")).Body;
        await Assert.That(moved.Branch).IsEqualTo("release");

        var paused = ((Contract.BindingsSetPollingResponse.Ok)await api.SetPolling(id, false)).Body;
        await Assert.That(paused.Polls).IsFalse();

        var got = ((Contract.BindingsGetResponse.Ok)await api.GetBinding(id)).Body;
        await Assert.That((got.CredentialsSecret, got.Branch, got.Polls)).IsEqualTo(((string?)null, "release", false));
    }

    [Test]
    public async Task BlankBranch_IsInvalid()
    {
        await using var api = Build();
        var id = (await api.Bound()).PipelineId;

        var response = await api.SetBranch(id, " ");

        await AssertProblem(((Contract.BindingsSetBranchResponse.BadRequest)response).Body, 400, "Binding requires a branch.");
    }

    [Test]
    public async Task EveryOperation_OnAnUnboundPipeline_IsNotFound()
    {
        await using var api = Build();
        var expected = NoBinding(Unknown);

        await AssertProblem(((Contract.BindingsGetResponse.NotFound)await api.GetBinding(Unknown)).Body, 404, expected);
        await AssertProblem(((Contract.BindingsStatusResponse.NotFound)await api.Status(Unknown)).Body, 404, expected);
        await AssertProblem(((Contract.BindingsUpdateResponse.NotFound)await api.Update(Unknown, "T")).Body, 404, expected);
        await AssertProblem(((Contract.BindingsSetBranchResponse.NotFound)await api.SetBranch(Unknown, "main")).Body, 404, expected);
        await AssertProblem(((Contract.BindingsSetPollingResponse.NotFound)await api.SetPolling(Unknown, false)).Body, 404, expected);
        await AssertProblem(((Contract.BindingsReconcileResponse.NotFound)await api.Reconcile(Unknown, wait: true)).Body, 404, expected);
        await AssertProblem(((Contract.BindingsReconcileResponse.NotFound)await api.Reconcile(Unknown, wait: false)).Body, 404, expected);
    }

    [Test]
    public async Task Status_ReportsTheLastReconcileAndTheActivation()
    {
        await using var api = Build();
        var bound = await api.Bound();
        var pipeline = new Id<Pipeline>(new Id(bound.PipelineId));
        var version = TestRunDomain.Pick(api.Get<PipelineVersionService>().Add(pipeline, """{"apiVersion":"2.0"}""", "sha256:a", "cfg-1",
            new Dictionary<string, int>(), new Dictionary<string, int>()));
        TestRunDomain.Pick(api.Get<PipelineActivationService>().BeginSwitch(pipeline, version.Id));
        TestRunDomain.Pick(api.Bindings.SetReconcileStatus(new Id<PipelineConfigBinding>(new Id(bound.Id)),
            new ReconcileStatus(ReconcileResult.Success, DateTimeOffset.UnixEpoch, [], ["[W] unused output"], "cfg-1", VersionNumber: 7)));

        var status = ((Contract.BindingsStatusResponse.Ok)await api.Status(bound.PipelineId)).Body;

        await Assert.That(status.Repo).IsEqualTo("acme/widgets");
        await Assert.That(status.Result).IsEqualTo(Contract.ReconcileResult.Success);
        await Assert.That(status.ReconcileSha).IsEqualTo("cfg-1");
        await Assert.That(status.ReconciledVersion).IsEqualTo(7);
        await Assert.That(status.Warnings).IsEquivalentTo(["[W] unused output"]);
        await Assert.That(((Contract.PipelineActivationActive)status.Activation!).VersionId).IsEqualTo(version.Id.Value.Value);
    }

    [Test]
    [Arguments(ReconcileResult.Success, 1)]
    [Arguments(ReconcileResult.Error, null)]
    public async Task Status_WithoutARecordedVersion_FindsItByCommit_AfterASuccess(ReconcileResult result, int? expected)
    {
        await using var api = Build();
        var bound = await api.Bound();
        var pipeline = new Id<Pipeline>(new Id(bound.PipelineId));
        TestRunDomain.Pick(api.Get<PipelineVersionService>().Add(pipeline, """{"apiVersion":"2.0"}""", "sha256:a", "cfg-1",
            new Dictionary<string, int>(), new Dictionary<string, int>()));
        TestRunDomain.Pick(api.Bindings.SetReconcileStatus(new Id<PipelineConfigBinding>(new Id(bound.Id)),
            new ReconcileStatus(result, DateTimeOffset.UnixEpoch, [], Sha: "cfg-1")));

        var status = ((Contract.BindingsStatusResponse.Ok)await api.Status(bound.PipelineId)).Body;

        await Assert.That(status.ReconciledVersion).IsEqualTo(expected);
    }

    [Test]
    public async Task ReconcileAndWait_AppliesTheCommit_AndRebuildAppliesItAgain()
    {
        await using var api = Build();
        var id = (await api.Bound()).PipelineId;
        api.Source.ConfigSha = "cfg-1";

        await Assert.That(await api.Reconcile(id)).IsTypeOf<Contract.BindingsReconcileResponse.Ok>();
        await Assert.That(api.Reconciler.Calls).IsEquivalentTo(["cfg-1"]);

        await Assert.That(await api.Reconcile(id)).IsTypeOf<Contract.BindingsReconcileResponse.Ok>();
        await Assert.That(api.Reconciler.Calls).IsEquivalentTo(["cfg-1"]);

        await Assert.That(await api.Reconcile(id, rebuild: true)).IsTypeOf<Contract.BindingsReconcileResponse.Ok>();
        await Assert.That(api.Reconciler.Calls).IsEquivalentTo(["cfg-1", "cfg-1"]);
    }

    [Test]
    public async Task ReconcileAndWait_ThatFails_IsAConflictWithTheReason()
    {
        await using var api = Build();
        var id = (await api.Bound()).PipelineId;
        api.Source.ConfigSha = "cfg-1";
        api.Reconciler.Failure = "the pipeline is invalid";

        var response = await api.Reconcile(id);

        var problem = ((Contract.BindingsReconcileResponse.Conflict)response).Body;
        await Assert.That(problem.Status).IsEqualTo(409);
        await Assert.That(problem.Detail!).StartsWith("Reconcile of cfg-1 failed: ");
        await Assert.That(problem.Detail!).Contains("the pipeline is invalid");
    }

    [Test]
    public async Task ReconcileWithoutWaiting_ReturnsAtOnce_AndRunsInTheBackground()
    {
        await using var api = Build();
        var id = (await api.Bound()).PipelineId;
        api.Source.ConfigSha = "cfg-1";

        await Assert.That(await api.Reconcile(id, wait: false)).IsTypeOf<Contract.BindingsReconcileResponse.Ok>();

        await Assert.That(await api.Reconciler.Called.Task.WaitAsync(TimeSpan.FromSeconds(30))).IsEqualTo("cfg-1");

        // Queues behind the background sync, and finds the commit applied.
        await Assert.That(await api.Reconcile(id)).IsTypeOf<Contract.BindingsReconcileResponse.Ok>();
        await Assert.That(api.Reconciler.Calls).IsEquivalentTo(["cfg-1"]);
    }
}
