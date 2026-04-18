namespace Olve.Pipelines.Pipelines.Sync;

public static class PipelineDocumentVersion
{
    public const int CurrentMajor = 0;
    public const int CurrentMinor = 0;
    public const string Current = "0.0";

    public static Result<(int Major, int Minor)> TryParse(string apiVersion)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return new ResultProblem("ApiVersion is required.");

        var parts = apiVersion.Split('.');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var major)
            || !int.TryParse(parts[1], out var minor)
            || major < 0
            || minor < 0)
        {
            return new ResultProblem($"ApiVersion '{apiVersion}' is not in 'major.minor' form.");
        }

        return (major, minor);
    }

    public static Result EnsureCompatible(string apiVersion)
    {
        var parsed = TryParse(apiVersion);
        if (parsed.TryPickProblems(out var problems, out var version))
            return problems;

        if (version.Major != CurrentMajor)
            return new ResultProblem(
                $"ApiVersion '{apiVersion}' is incompatible: expected major {CurrentMajor}, got {version.Major}.");

        return Result.Success();
    }
}
