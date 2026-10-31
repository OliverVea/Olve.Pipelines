using Olve.Pipelines.Recorder.Recordings;

namespace Olve.Pipelines.UnitTests.Recordings;

public static class RecordingSummaryHook
{
    [After(HookType.TestSession)]
    public static void AfterRun() => Run(AppContext.BaseDirectory, TimeProvider.System, Console.Out);

    /// <summary>An out-of-date recording must never fail a build, so this reports and returns.</summary>
    internal static void Run(string start, TimeProvider clock, TextWriter output)
    {
        var line = Freshness.Report(start, clock);
        output.WriteLine(line);
        Freshness.Publish(start, line);
    }
}
