namespace Olve.Pipelines.GitHub;

/// <summary>
/// Where GitHub is: <c>GitHub:ApiUrl</c> (config cursors, branch heads, private tarballs) and
/// <c>GitHub:CodeloadUrl</c> (public tarballs). Defaults are github.com; a GitHub Enterprise host or a
/// local stand-in for tests goes here.
/// </summary>
public static class GitHubEndpoints
{
    public const string DefaultApiUrl = "https://api.github.com";
    public const string DefaultCodeloadUrl = "https://codeload.github.com";

    public static string ApiUrl(IConfiguration configuration) => Url(configuration["GitHub:ApiUrl"], DefaultApiUrl);

    public static string CodeloadUrl(IConfiguration configuration) => Url(configuration["GitHub:CodeloadUrl"], DefaultCodeloadUrl);

    private static string Url(string? configured, string fallback)
        => string.IsNullOrWhiteSpace(configured) ? fallback : configured.TrimEnd('/');
}
