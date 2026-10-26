using System.Security.Claims;

namespace Olve.Pipelines.Configuration;

/// <summary>
/// Who may use the API besides holding a valid token (<c>Auth:Allow</c>). Empty lets any valid token in,
/// which is only safe while the identity provider itself limits who can sign in; Dex with an upstream
/// such as GitHub does not.
/// </summary>
/// <param name="Subjects">Stable subject ids, matched exactly against <c>sub</c>; preferred over logins, which can be renamed and re-registered.</param>
/// <param name="Users">Logins, matched against <c>preferred_username</c>.</param>
/// <param name="Orgs">An org (any of its teams) or <c>org:team</c>, matched against the <c>groups</c> claim.</param>
/// <param name="Emails">Verified email addresses.</param>
public sealed record AuthAllowList(IReadOnlyList<string> Subjects, IReadOnlyList<string> Users, IReadOnlyList<string> Orgs, IReadOnlyList<string> Emails)
{
    public bool IsEmpty => Subjects.Count == 0 && Users.Count == 0 && Orgs.Count == 0 && Emails.Count == 0;

    public static AuthAllowList From(IConfiguration configuration) => new(
        Values(configuration, "Auth:Allow:Subjects"),
        Values(configuration, "Auth:Allow:Users"),
        Values(configuration, "Auth:Allow:Orgs"),
        Values(configuration, "Auth:Allow:Emails"));

    public bool Allows(ClaimsPrincipal user)
    {
        if (IsEmpty) return true;

        if (Subject(user) is { } subject && Subjects.Contains(subject, StringComparer.Ordinal))
            return true;

        if (user.FindAll("preferred_username").Any(c => Users.Contains(c.Value, StringComparer.OrdinalIgnoreCase)))
            return true;

        if (user.FindAll("groups").Any(c => Orgs.Any(org => InOrg(c.Value, org))))
            return true;

        var verified = string.Equals(user.FindFirst("email_verified")?.Value, "true", StringComparison.OrdinalIgnoreCase);
        return verified && user.FindAll(c => c.Type is "email" or ClaimTypes.Email)
            .Any(c => Emails.Contains(c.Value, StringComparer.OrdinalIgnoreCase));
    }

    public static string? Subject(ClaimsPrincipal user) =>
        (user.FindFirst("sub") ?? user.FindFirst(ClaimTypes.NameIdentifier))?.Value;

    private static bool InOrg(string group, string allowed) =>
        string.Equals(group, allowed, StringComparison.OrdinalIgnoreCase)
        || (!allowed.Contains(':') && group.StartsWith(allowed + ":", StringComparison.OrdinalIgnoreCase));

    private static string[] Values(IConfiguration configuration, string key) =>
        configuration.GetSection(key).GetChildren()
            .Select(c => c.Value?.Trim())
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)
            .ToArray();
}
