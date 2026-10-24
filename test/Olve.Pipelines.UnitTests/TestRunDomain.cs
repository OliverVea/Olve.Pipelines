using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Shared;
using Olve.Results;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests;

/// <summary>
/// The v2 run domain hand-wired over fresh stores (no DI container): pipelines, versions, activation,
/// sequences, runs and attempts, plus builders that create a pipeline → version → run chain on demand.
/// <see cref="WireEvents"/> adds the production event wiring (hub forwarding, cascade cleanup,
/// latest-wins obsoletion) through the real <see cref="IRunOnStartup"/> registrations.
/// </summary>
internal sealed class TestRunDomain
{
    public TestRunDomain(TimeProvider? time = null)
    {
        Time = time ?? new MonotonicTimeProvider();
        Pipelines = new EntityStore<Pipeline>([]);
        Versions = new EntityStore<PipelineVersion>([]);
        Runs = new EntityStore<Run>([]);
        Attempts = new EntityStore<Attempt>([]);
        Activations = new AttachmentStore<Pipeline, PipelineActivation>(Pipelines);
        Sequences = new AttachmentStore<Pipeline, PipelineSequences>(Pipelines);
        BlockedSteps = new AttachmentStore<Pipeline, PipelineBlockedSteps>(Pipelines);
        PendingStarts = new AttachmentStore<Pipeline, PendingRunStart>(Pipelines);
        EntityVersions = new AttachmentStore<Pipeline, EntityVersionMap>(Pipelines);
        PollTriggers = new AttachmentStore<Pipeline, PollTriggerStates>(Pipelines);
        ConfigSnapshotStore = new EntityStore<ConfigSnapshot>([]);
        PendingVersions = new AttachmentStore<Pipeline, PendingVersion>(Pipelines);

        var ids = new IdProvider();
        PipelineService = new PipelineService(Pipelines, ids);
        Numbering = new PipelineNumberingService(Sequences);
        VersionService = new PipelineVersionService(Versions, Pipelines, Numbering, ids, Time);
        ActivationService = new PipelineActivationService(Activations, VersionService, Time);
        RunService = new RunService(Runs, Versions, Numbering, ids, Time);
        AttemptService = new AttemptService(Attempts, Runs, ids, Time);
        Query = new RunQueryService(RunService, AttemptService);
        Plans = new RunPlanCache(VersionService);
        ConfigSnapshots = new ConfigSnapshotService(ConfigSnapshotStore, Pipelines, ids, Time);
    }

    public EntityStore<ConfigSnapshot> ConfigSnapshotStore { get; }
    public AttachmentStore<Pipeline, PendingVersion> PendingVersions { get; }
    public ConfigSnapshotService ConfigSnapshots { get; }
    public ConfigActivationService Gate => Services.GetRequiredService<ConfigActivationService>();

    public AttachmentStore<Pipeline, PipelineBlockedSteps> BlockedSteps { get; }
    public AttachmentStore<Pipeline, PendingRunStart> PendingStarts { get; }
    public AttachmentStore<Pipeline, EntityVersionMap> EntityVersions { get; }
    public AttachmentStore<Pipeline, PollTriggerStates> PollTriggers { get; }
    public RunSchedulingLock SchedulingLock { get; } = new();
    public RunPlanCache Plans { get; }

    /// <summary>Resolves the scheduler services (available after <see cref="WireEvents"/>).</summary>
    public IServiceProvider Services => _services ?? throw new InvalidOperationException("Call WireEvents() first.");
    private IServiceProvider? _services;

    public StageScheduler Scheduler => Services.GetRequiredService<StageScheduler>();
    public RunStartService Starts => Services.GetRequiredService<RunStartService>();
    public SwitchService Switches => Services.GetRequiredService<SwitchService>();
    public RunContinuationService Continuations => Services.GetRequiredService<RunContinuationService>();
    public StepBlockService GateService => Services.GetRequiredService<StepBlockService>();
    public AttemptLaunchService Launches => Services.GetRequiredService<AttemptLaunchService>();

    public TimeProvider Time { get; }
    public EntityStore<Pipeline> Pipelines { get; }
    public EntityStore<PipelineVersion> Versions { get; }
    public EntityStore<Run> Runs { get; }
    public EntityStore<Attempt> Attempts { get; }
    public AttachmentStore<Pipeline, PipelineActivation> Activations { get; }
    public AttachmentStore<Pipeline, PipelineSequences> Sequences { get; }

    public PipelineService PipelineService { get; }
    public PipelineNumberingService Numbering { get; }
    public PipelineVersionService VersionService { get; }
    public PipelineActivationService ActivationService { get; }
    public RunService RunService { get; }
    public AttemptService AttemptService { get; }
    public RunQueryService Query { get; }

    public PipelineEvents PipelineEvents { get; } = new();
    public RunEvents RunEvents { get; } = new();
    public AttemptEvents AttemptEvents { get; } = new();
    public PipelineVersionEvents VersionEvents { get; } = new();

    /// <summary>
    /// Wires store → hub forwarding, cascade cleanup and obsoletion exactly as in production; with
    /// <paramref name="scheduler"/>, also the stage scheduler's subscriptions (run creation, attempt
    /// completion, blocked-step changes).
    /// </summary>
    public TestRunDomain WireEvents(bool scheduler = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(RunService);
        services.AddSingleton(AttemptService);
        services.AddSingleton(VersionService);
        services.AddTransient<RunCleanupService>();
        services.AddTransient<PipelineVersionCleanupService>();
        services.AddTransient<JobObsoletionService>();
        services.AddSingleton(Time);
        services.AddSingleton(Pipelines);
        services.AddSingleton(Runs);
        services.AddSingleton(Attempts);
        services.AddSingleton(BlockedSteps);
        services.AddSingleton(PendingStarts);
        services.AddSingleton(ActivationService);
        services.AddSingleton(SchedulingLock);
        services.AddSingleton(Plans);
        services.AddSingleton(ConfigSnapshots);
        services.AddSingleton(PendingVersions);
        services.AddTransient<ConfigActivationService>();
        services.AddTransient<StepBlockService>();
        services.AddTransient<StageScheduler>();
        services.AddTransient<RunStartService>();
        services.AddTransient<SwitchService>();
        services.AddTransient<RunContinuationService>();
        services.AddTransient<AttemptLaunchService>();
        services.AddTransient<RunSchedulingHandler>();
        var sp = services.BuildServiceProvider();
        _services = sp;

        var registrations = new IRunOnStartup[]
        {
            new PipelineEventRegistration(Pipelines, PipelineEvents),
            new PipelineVersionEventRegistration(Versions, VersionEvents, PipelineEvents, sp),
            new RunEventRegistration(Runs, Attempts, RunEvents, AttemptEvents, PipelineEvents, sp),
            new JobEventRegistration(AttemptEvents, sp),
        };
        if (scheduler)
            registrations = [.. registrations, new RunSchedulingEventRegistration(RunEvents, AttemptEvents, BlockedSteps, sp)];
        foreach (var registration in registrations)
        {
            if (registration.Run().TryPickProblems(out var problems))
                throw new InvalidOperationException(string.Join("; ", problems.Select(p => p.ToString())));
        }

        return this;
    }

    /// <summary>Read models over this domain (no event wiring needed).</summary>
    public RunViewService Views => new(RunService, AttemptService, VersionService, ActivationService, Plans,
        new StepBlockService(BlockedSteps, Pipelines, SchedulingLock));

    public PipelineHealthService Health => new(RunService, Views, ActivationService, new StepBlockService(BlockedSteps, Pipelines, SchedulingLock), PendingVersions);

    public Pipeline CreatePipeline(string name = "p") => Pick(PipelineService.Create(name));

    public PipelineVersion AddVersion(
        Id<Pipeline> pipelineId,
        string manifest = """{"apiVersion":"2.0"}""",
        string hash = "sha256:abc",
        string? commit = "deadbeef",
        IReadOnlyDictionary<string, int>? steps = null,
        IReadOnlyDictionary<string, int>? models = null)
        => Pick(VersionService.Add(pipelineId, manifest, hash, commit,
            steps ?? new Dictionary<string, int> { ["build"] = 1 },
            models ?? new Dictionary<string, int> { ["Source"] = 1 }));

    /// <summary>A run on <paramref name="version"/>, or on a fresh pipeline + version when null.</summary>
    public Run CreateRun(PipelineVersion? version = null, string input = """{"sha":"abc"}""",
        RunTrigger? trigger = null, Id<Run>? rerunOf = null)
    {
        version ??= AddVersion(CreatePipeline().Id);
        return Pick(RunService.Create(version.PipelineId, version.Id, input, trigger ?? new RunTrigger(RunTriggerKind.Manual), rerunOf));
    }

    /// <summary>Schedules an attempt in <paramref name="run"/>, or in a run of a fresh pipeline when null.</summary>
    public Attempt Schedule(string stepName = "build", Run? run = null)
        => Pick(AttemptService.Schedule((run ?? CreateRun()).Id, stepName));

    public Attempt Get(Id<Attempt> id)
    {
        Attempts.TryGet(id, out var attempt);
        return attempt!;
    }

    public static T Pick<T>(Result<T> result)
    {
        if (result.TryPickProblems(out var problems, out var value))
            throw new InvalidOperationException(string.Join("; ", problems.Select(p => p.ToString())));
        return value;
    }
}

/// <summary>Builds <see cref="Attempt"/> records directly (for tests that seed a store without a run).</summary>
internal static class TestAttempts
{
    public static Attempt Make(
        Id<Pipeline>? pipelineId = null,
        string stepName = "build",
        DateTimeOffset? createdAt = null,
        AttemptStatus? status = null,
        int runNumber = 1,
        int number = 1)
        => new(
            Id.New<Attempt>(),
            pipelineId ?? Id.New<Pipeline>(),
            Id.New<Run>(),
            runNumber,
            stepName,
            number,
            status ?? new AttemptStatus.Scheduled(),
            createdAt ?? DateTimeOffset.UtcNow);
}
