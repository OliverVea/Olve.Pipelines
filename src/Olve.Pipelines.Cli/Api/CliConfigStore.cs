using System.Text.Json;

namespace Olve.Pipelines.Cli.Api;

/// <summary>
/// Reads/writes <see cref="CliConfig"/> at <c>~/.pl</c> (overridable via <c>PIPELINES_CONFIG</c>
/// for tests). The file is written <c>0600</c> since it holds bearer tokens.
/// </summary>
public static class CliConfigStore
{
    public const string PathEnvVar = "PIPELINES_CONFIG";

    public static string ResolvePath()
    {
        var overridePath = Environment.GetEnvironmentVariable(PathEnvVar);
        if (!string.IsNullOrWhiteSpace(overridePath))
            return overridePath;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".pl");
    }

    /// <summary>Loads the config, or an empty one if the file is absent. A malformed file is a problem.</summary>
    public static Result<CliConfig> Load()
    {
        var path = ResolvePath();
        if (!File.Exists(path))
            return Result.Success(new CliConfig());

        try
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize(json, CliJsonContext.Default.CliConfig);
            return Result.Success(config ?? new CliConfig());
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new ResultProblem("Could not read config '{0}': {1}", path, ex.Message);
        }
    }

    public static Result Save(CliConfig config)
    {
        var path = ResolvePath();
        try
        {
            var json = JsonSerializer.Serialize(config, CliJsonContext.Default.CliConfig);
            File.WriteAllText(path, json);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ResultProblem("Could not write config '{0}': {1}", path, ex.Message);
        }
    }
}
