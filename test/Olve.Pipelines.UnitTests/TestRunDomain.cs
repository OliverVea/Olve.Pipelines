using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;
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

        var ids = new IdProvider();
        PipelineService = new PipelineService(Pipelines, ids);
        Numbering = new PipelineNumberingService(Sequences);
        VersionService = new PipelineVersionService(Versions, Pipelines, Numbering, ids, Time);
        ActivationService = new PipelineActivationService(Activations, VersionService, Time);
        RunService = new RunService(Runs, Versions, Numbering, ids, Time);
        AttemptService = new AttemptService(Attempts, Runs, ids, Time);
        Query = new RunQueryService(RunService, AttemptService, VersionService);
    }

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

    /// <summary>Wires store → hub forwarding, cascade cleanup and obsoletion exactly as in production.</summary>
    public TestRunDomain WireEvents()
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
        var sp = services.BuildServiceProvider();

        var registrations = new IRunOnStartup[]
        {
            new PipelineEventRegistration(Pipelines, PipelineEvents),
            new PipelineVersionEventRegistration(Versions, VersionEvents, PipelineEvents, sp),
            new RunEventRegistration(Runs, Attempts, RunEvents, AttemptEvents, PipelineEvents, sp),
            new JobEventRegistration(AttemptEvents, sp),
        };
        foreach (var registration in registrations)
        {
            if (registration.Run().TryPickProblems(out var problems))
                throw new InvalidOperationException(string.Join("; ", problems.Select(p => p.ToString())));
        }

        return this;
    }

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
