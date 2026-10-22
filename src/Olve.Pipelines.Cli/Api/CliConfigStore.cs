using System.Text.Json;

namespace Olve.Pipelines.Cli.Api;

/// <summary>
/// Reads/writes the CLI config at <c>~/.pl</c> (overridable via <c>PIPELINES_CONFIG</c> for tests): named
/// profiles, picked by <c>--profile</c> &gt; <c>PIPELINES_PROFILE</c> &gt; <c>default</c>. A pre-profile file
/// (top-level <c>apiUrl</c>/<c>auth</c>) reads as <c>default</c> and is rewritten with profiles on the next
/// save. The file is written <c>0600</c> since it holds bearer tokens.
/// </summary>
public static class CliConfigStore
{
    public const string PathEnvVar = "PIPELINES_CONFIG";
    public const string ProfileEnvVar = "PIPELINES_PROFILE";
    public const string DefaultProfile = "default";

    public static string ResolvePath()
    {
        var overridePath = Environment.GetEnvironmentVariable(PathEnvVar);
        if (!string.IsNullOrWhiteSpace(overridePath))
            return overridePath;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".pl");
    }

    public static string ResolveProfile(CliArgs cli)
    {
        if (cli.Option("profile") is { Length: > 0 } flag)
            return flag;
        if (Environment.GetEnvironmentVariable(ProfileEnvVar) is { Length: > 0 } env)
            return env;
        return DefaultProfile;
    }

    /// <summary>Loads <paramref name="profile"/>, or an empty one if it (or the file) is absent. A malformed file is a problem.</summary>
    public static Result<CliConfig> Load(string profile = DefaultProfile)
    {
        if (LoadProfiles().TryPickProblems(out var problems, out var profiles))
            return problems;

        var config = profiles.GetValueOrDefault(profile) ?? new CliConfig();
        config.Profile = profile;
        return config;
    }

    /// <summary>Every profile in the file, by name.</summary>
    public static Result<Dictionary<string, CliConfig>> LoadProfiles()
    {
        var path = ResolvePath();
        if (!File.Exists(path))
            return new Dictionary<string, CliConfig>(StringComparer.Ordinal);

        try
        {
            var file = JsonSerializer.Deserialize(File.ReadAllText(path), CliJsonContext.Default.CliConfigFile) ?? new CliConfigFile();
            var profiles = new Dictionary<string, CliConfig>(file.Profiles ?? [], StringComparer.Ordinal);
            if (file.Profiles is null && (file.ApiUrl is not null || file.Auth is not null))
                profiles[DefaultProfile] = new CliConfig { ApiUrl = file.ApiUrl, Auth = file.Auth };
            foreach (var (name, config) in profiles)
                config.Profile = name;
            return profiles;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new ResultProblem("Could not read config '{0}': {1}", path, ex.Message);
        }
    }

    /// <summary>Writes <paramref name="config"/> as its profile, keeping the other profiles.</summary>
    public static Result Save(CliConfig config)
    {
        if (LoadProfiles().TryPickProblems(out var problems, out var profiles))
            return problems;
        profiles[config.Profile] = config;

        var path = ResolvePath();
        try
        {
            var json = JsonSerializer.Serialize(new CliConfigFile { Profiles = profiles }, CliJsonContext.Default.CliConfigFile);
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
