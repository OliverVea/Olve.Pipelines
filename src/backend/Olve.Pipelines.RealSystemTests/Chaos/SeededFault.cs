namespace Olve.Pipelines.RealSystemTests.Chaos;

/// <summary>A point in a run's life: the server log line that marks it, and how long after it a fault may land.</summary>
public sealed record FaultMoment(string Name, string LogLine, int MaxDelayMs = SeededFault.FlushWindowMs);

/// <summary>One fault, fully decided by its seed, so a failure replays with the same moment, delay and downtime.</summary>
public sealed record SeededFault(int Seed, FaultMoment Moment, TimeSpan Delay, TimeSpan Downtime)
{
    /// <summary>Past the server's 1 s flush timer, so a fault lands either side of a flush.</summary>
    public const int FlushWindowMs = 1500;

    /// <summary>Long enough for a running step to finish while the fault lasts.</summary>
    public const int MaxDowntimeMs = 8000;

    /// <summary>A step's pod takes seconds, so these reach into the running step.</summary>
    private const int RunningStepMs = 5000;

    public static IReadOnlyList<FaultMoment> Moments { get; } =
    [
        new("run accepted", @"Started run #1 "),
        .. StepMoments("produce"),
        .. StepMoments("consume"),
        new("run completed", @"Run #1 \(.* succeeded"),
    ];

    private static IEnumerable<FaultMoment> StepMoments(string step) =>
    [
        new($"{step} scheduled", $"Scheduled step '{step}'"),
        new($"{step} Job created", $"Created K8s Job '[^']+' for step '{step}'"),
        new($"{step} in progress", $"Created K8s Job '[^']+' for step '{step}'", RunningStepMs),
        new($"{step} result collected", $"Collected result of step '{step}'"),
        new($"{step} finished", $"Attempt 1 of step '{step}' .* finished"),
    ];

    /// <summary>Consecutive seeds walk the moments in order, so a round of them covers every moment.</summary>
    /// <param name="minDowntimeMs">Zero: the fault mostly restarts at once. Above: every fault lasts at least this long.</param>
    public static SeededFault FromSeed(int seed, int minDowntimeMs = 0)
    {
        var random = new Random(seed);
        var moment = Moments[(int)((uint)seed % Moments.Count)];
        var delay = TimeSpan.FromMilliseconds(random.Next(moment.MaxDelayMs));
        var downtimeMs = minDowntimeMs > 0
            ? random.Next(minDowntimeMs, MaxDowntimeMs)
            : random.Next(3) == 0 ? random.Next(MaxDowntimeMs) : 0;
        return new SeededFault(seed, moment, delay, TimeSpan.FromMilliseconds(downtimeMs));
    }

    public override string ToString() =>
        $"seed {Seed}: {Delay.TotalMilliseconds:0} ms after '{Moment.Name}', lasting {Downtime.TotalMilliseconds:0} ms";
}
