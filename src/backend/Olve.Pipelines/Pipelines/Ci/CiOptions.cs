namespace Olve.Pipelines.Pipelines.Ci;

/// <summary>
/// <c>Ci:Authors</c>: the GitHub logins whose pull requests get CI. CI runs a pull request's own code, so a pull request
/// by anyone else is treated like one from a fork. Empty, no pull request gets CI.
/// </summary>
public sealed record CiOptions(IReadOnlyList<string> Authors)
{
    public bool Allows(string? login) =>
        login is not null && Authors.Contains(login, StringComparer.OrdinalIgnoreCase);
}
