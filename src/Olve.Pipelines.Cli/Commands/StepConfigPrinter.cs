using Olve.Pipelines.Cli.Api.Contracts;
using Olve.Pipelines.Cli.Output;

namespace Olve.Pipelines.Cli.Commands;

/// <summary>Human rendering of a <see cref="StepConfiguration"/>, shared by production/processing config commands.</summary>
public static class StepConfigPrinter
{
    public static void Print(IOutputWriter output, StepConfiguration config)
    {
        output.Line($"Image:  {config.Image}");
        output.Line($"Script: {config.Script}");
        if (config.EnvironmentVariables.Count == 0)
            return;

        output.Line("Env:");
        foreach (var (key, value) in config.EnvironmentVariables.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            output.Line($"  {key}={value}");
    }
}
