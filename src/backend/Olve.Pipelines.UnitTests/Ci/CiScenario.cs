using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.GitHub;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Ci;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Shared;
using Olve.Pipelines.UnitTests.Manifests;
using Olve.Results;
using Olve.Utilities.Ids;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.UnitTests.Ci;

/// <summary>A clock tests move by hand: <see cref="GetUtcNow"/> and timers (<c>Task.Delay</c>) follow <see cref="Advance"/>.</summary>
internal sealed class CiClock : TimeProvider
{
    private TimeSpan _elapsed;

    public static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-15T10:00:00Z");

    public override DateTimeOffset GetUtcNow() => Start + _elapsed;

    public void Advance(TimeSpan by)
    {
        List<Action> due = [];
        lock (_timers)
        {
            _elapsed += by;
            foreach (var timer in _timers.Where(t => t.Due <= _elapsed).ToList())
            {
                _timers.Remove(timer);
                due.Add(timer.Fire);
            }
        }

        foreach (var fire in due) fire();
    }

    private readonly List<Timer> _timers = [];

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, () => callback(state));
        timer.Change(dueTime, period);
        return timer;
    }

    private sealed class Timer(CiClock clock, Action fire) : ITimer
    {
        public TimeSpan Due { get; private set; }

        public void Fire() => fire();

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._timers)
            {
                clock._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan) return true;
                Due = clock._elapsed + dueTime;
                clock._timers.Add(this);
            }

            return true;
        }

        public void Dispose()
        {
            lock (clock._timers) clock._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Timers waiting.</summary>
    public int Pending
    {
        get
        {
            lock (_timers) return _timers.Count;
        }
    }
}

/// <summary>GitHub as CI reads it: each pull request's answers in order (the last repeats), commits and comparisons.</summary>
internal sealed class FakeGitHub : IPullRequestSource
{
    public Dictionary<int, Queue<GitHubPullRequest>> Pulls { get; } = [];
    public Dictionary<string, GitHubGitCommit> Commits { get; } = [];
    public Dictionary<string, IReadOnlyList<string>> ChangedFiles { get; } = [];

    public void Answer(params GitHubPullRequest[] answers) =>
        Pulls[answers[0].Number] = new Queue<GitHubPullRequest>(answers);

    public Task<Result<IReadOnlyList<GitHubPullRequest>>> ListOpenAsync(PipelineConfigBinding binding, CancellationToken ct = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<GitHubPullRequest>>([.. Pulls.Values.Select(q => q.Peek())]));

    public Task<Result<GitHubPullRequest?>> GetAsync(PipelineConfigBinding binding, int number, CancellationToken ct = default)
    {
        if (!Pulls.TryGetValue(number, out var answers))
            return Task.FromResult(Result.Success<GitHubPullRequest?>(null));
        return Task.FromResult(Result.Success<GitHubPullRequest?>(answers.Count > 1 ? answers.Dequeue() : answers.Peek()));
    }

    public Task<Result<GitHubGitCommit>> GetCommitAsync(PipelineConfigBinding binding, string sha, CancellationToken ct = default) =>
        Task.FromResult(Commits.TryGetValue(sha, out var commit)
            ? Result.Success(commit)
            : Result.Success(new GitHubGitCommit(sha, new GitHubShaRef("tree-" + sha), [new GitHubShaRef(CiScenario.MainSha)])));

    public Task<Result<IReadOnlyList<string>>> ChangedFilesAsync(PipelineConfigBinding binding, string @base, string head, CancellationToken ct = default) =>
        Task.FromResult(Result.Success(ChangedFiles.TryGetValue(head, out var files) ? files : (IReadOnlyList<string>)["src/app.cs"]));
}

/// <summary>GitHub's statuses endpoint: records each post; answers from <see cref="Outcomes"/> in order, then Posted.</summary>
internal sealed class FakeCommitStatuses : ICommitStatusClient
{
    private readonly Lock _gate = new();

    public List<(string Sha, CiCommitStatus Status)> Posted { get; } = [];
    public Queue<(CommitStatusOutcome, int?)> Outcomes { get; } = new();

    /// <summary>Runs before each post answers (e.g. a newer state arriving while one is in flight).</summary>
    public Action? DuringPost { get; set; }

    public Task<(CommitStatusOutcome Outcome, int? Code)> PostAsync(PipelineConfigBinding binding, string sha, CiCommitStatus status, CancellationToken ct)
    {
        DuringPost?.Invoke();
        lock (_gate)
        {
            var answer = Outcomes.Count > 0 ? Outcomes.Dequeue() : (CommitStatusOutcome.Posted, 201);
            if (answer.Item1 == CommitStatusOutcome.Posted)
                Posted.Add((sha, status));
            return Task.FromResult(answer);
        }
    }
}

/// <summary>Builds of pull requests' own definitions, finished by the test: each commit's build waits for <see cref="Finish"/>.</summary>
internal sealed class FakeCiBuilder : ICiDefinitionBuilder
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, TaskCompletionSource<Result<string>>> _builds = [];
    private readonly Dictionary<string, TaskCompletionSource> _started = [];

    public List<string> Started { get; } = [];
    public List<string> Deleted { get; } = [];
    public HashSet<string> Binaries { get; } = [];

    private TaskCompletionSource<Result<string>> For(string sha)
    {
        lock (_gate)
        {
            if (!_builds.TryGetValue(sha, out var tcs))
                _builds[sha] = tcs = new TaskCompletionSource<Result<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            return tcs;
        }
    }

    /// <summary>Completes once the build of <paramref name="sha"/> has started.</summary>
    public Task WhenStarted(string sha) => StartedSignal(sha).Task;

    private TaskCompletionSource StartedSignal(string sha)
    {
        lock (_gate)
        {
            if (!_started.TryGetValue(sha, out var tcs))
                _started[sha] = tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return tcs;
        }
    }

    public void Finish(string sha, Result<string> result)
    {
        For(sha).SetResult(result);
        if (!result.TryPickProblems(out _))
            lock (_gate) Binaries.Add(sha);
    }

    public Task<Result<string>> BuildAsync(PipelineConfigBinding binding, string commitSha, CancellationToken ct)
    {
        lock (_gate) Started.Add(commitSha);
        StartedSignal(commitSha).TrySetResult();
        return For(commitSha).Task.WaitAsync(ct);
    }

    public Task DeleteAsync(Id<Pipeline> pipelineId, string commitSha)
    {
        lock (_gate) Deleted.Add(commitSha);
        return Task.CompletedTask;
    }

    public Task<bool> BinaryExistsAsync(Id<Pipeline> pipelineId, string commitSha, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(Binaries.Contains(commitSha));
    }
}

/// <summary>
/// The production wiring (<see cref="ServiceConfiguration.AddPipelineServices"/>) with a bound pipeline whose active
/// version declares CI (the fixture's checkout, then build and test), GitHub faked and time moved by hand.
/// </summary>
internal sealed class CiScenario : IAsyncDisposable
{
    public const string MainSha = "1111111111111111111111111111111111111111";
    public const string BuiltSha = "2222222222222222222222222222222222222222";

    public CiScenario(Action<JsonObject>? manifest = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IPullRequestSource>(GitHub);
        services.AddSingleton<ICiDefinitionBuilder>(Builder);
        services.AddSingleton<ICommitStatusClient>(Statuses);
        services.AddSingleton(new StorageOptions("bucket", Mode: StorageMode.Ephemeral));
        services.AddPipelineServices();
        Provider = services.BuildServiceProvider();
        foreach (var startup in Provider.GetServices<IRunOnStartup>().Where(s => s is PipelineEventRegistration or PipelineVersionEventRegistration
                     or RunEventRegistration or PullRequestEventRegistration or CiEventRegistration))
            Ok(startup.Run());

        Pipeline = TestRunDomain.Pick(Get<PipelineService>().Create("widgets"));
        Binding = TestRunDomain.Pick(Get<PipelineConfigBindingService>().Create(Pipeline.Id, PullRequestFixtures.Repo, "main", ".pipelines", null));
        Version = TestRunDomain.Pick(Get<PipelineVersionService>().Add(Pipeline.Id, ManifestFixture.Edit(node =>
        {
            ManifestFixture.WithCi(node);
            manifest?.Invoke(node);
        }), "sha256:" + Guid.NewGuid().ToString("N"), BuiltSha, new Dictionary<string, int>(), new Dictionary<string, int>()));
        TestRunDomain.Pick(Get<SwitchService>().Switch(Pipeline.Id, Version.Id));
    }

    public CiClock Clock { get; } = new();
    public FakeGitHub GitHub { get; } = new();
    public FakeCiBuilder Builder { get; } = new();
    public FakeCommitStatuses Statuses { get; } = new();
    public ServiceProvider Provider { get; }
    public Pipeline Pipeline { get; }
    public PipelineConfigBinding Binding { get; }
    public PipelineVersion Version { get; }

    public T Get<T>() where T : notnull => Provider.GetRequiredService<T>();

    public CiRunService Runs => Get<CiRunService>();
    public CiAttemptService Attempts => Get<CiAttemptService>();
    public CiTriggerService Trigger => Get<CiTriggerService>();

    /// <summary>A push to pull request <paramref name="number"/>, as the webhook reports it; GitHub then answers with it decided.</summary>
    public void Push(int number, string head, bool? mergeable = true, bool draft = false)
    {
        var pull = PullRequestFixtures.Pull(number, headSha: head, draft: draft, mergeable: mergeable, mergeCommitSha: Merge(head),
            updatedAt: Clock.GetUtcNow());
        GitHub.Answer(pull);
        Get<PullRequestService>().Observe(Pipeline.Id, "main", pull with { Mergeable = null, MergeCommitSha = null }, Clock.GetUtcNow());
    }

    public void Close(int number, string head, bool merged)
    {
        Clock.Advance(TimeSpan.FromSeconds(1));
        Get<PullRequestService>().Observe(Pipeline.Id, "main",
            PullRequestFixtures.Pull(number, headSha: head, state: "closed", merged: merged, updatedAt: Clock.GetUtcNow()), Clock.GetUtcNow());
    }

    public static string Merge(string head) => "m" + head[1..];

    /// <summary>Waits until the builds of own definitions in flight have settled their runs.</summary>
    public Task BuildsSettledAsync() => Get<CiDefinitionService>().InFlight();

    /// <summary>Lets the debounce pass and starts what is due.</summary>
    public async Task SettleAsync()
    {
        Clock.Advance(CiTriggerService.Debounce);
        await Trigger.StartDueAsync(CancellationToken.None);
    }

    public CiRun Run(int number) => Runs.ListByPull(Pipeline.Id, number).Last();

    public CiRun Get(CiRun run) => Runs.TryGet(run.Id, out var current) ? current : throw new InvalidOperationException("run is gone");

    public CiAttempt? Latest(CiRun run, string step) => Attempts.ListForStep(run.Id, step).LastOrDefault();

    public IReadOnlyList<string> Active(CiRun run) =>
        [.. Attempts.ListByRun(run.Id).Where(a => a.Status.IsActive()).Select(a => a.StepName).Order(StringComparer.Ordinal)];

    public void Succeed(CiRun run, string step, string? output = "{}")
    {
        var attempt = Latest(run, step) ?? throw new InvalidOperationException($"no attempt of '{step}'");
        Ok(Attempts.MarkInProgress(attempt.Id));
        Ok(Attempts.MarkSucceeded(attempt.Id, "key", output));
    }

    public void Fail(CiRun run, string step, string reason = "boom")
    {
        var attempt = Latest(run, step) ?? throw new InvalidOperationException($"no attempt of '{step}'");
        Ok(Attempts.MarkInProgress(attempt.Id));
        Ok(Attempts.MarkFailed(attempt.Id, reason, AttemptFailureKind.Handler));
    }

    public void Skip(CiRun run, string step, string reason)
    {
        var attempt = Latest(run, step) ?? throw new InvalidOperationException($"no attempt of '{step}'");
        Ok(Attempts.MarkSkipped(attempt.Id, reason));
    }

    public static void Ok(Result result)
    {
        if (result.TryPickProblems(out var problems))
            throw new InvalidOperationException(string.Join("; ", problems.Select(p => p.FormattedMessage)));
    }

    public ValueTask DisposeAsync() => Provider.DisposeAsync();
}
