using System.Net.Http.Headers;
using Olve.Pipelines.Cli.Diagnostics;

namespace Olve.Pipelines.Cli.Api;

/// <summary>
/// Builds the hand-rolled <see cref="IPipelinesApi"/> client, resolving the base URL and bearer
/// token with precedence: flag (<c>--api-url</c>/<c>--token</c>) &gt; env (<c>PIPELINES_API_URL</c>/
/// <c>PIPELINES_API_TOKEN</c>) &gt; <c>~/.pl</c> &gt; built-in default.
/// </summary>
public sealed class ApiClientFactory
{
    public const string DefaultApiUrl = "https://pipelines-private.ovea.pro";
    public const string ApiUrlEnvVar = "PIPELINES_API_URL";
    public const string TokenEnvVar = "PIPELINES_API_TOKEN";

    public static Result<IPipelinesApi> Create(CliArgs cli, CliConfig config, IConsoleLog log)
    {
        if (CreateTransport(cli, config, log).TryPickProblems(out var problems, out var transport))
            return problems;

        return Result.Success<IPipelinesApi>(new PipelinesApi(transport));
    }

    /// <summary>Builds the lower-level transport (used by the API client and the OIDC login flow).</summary>
    public static Result<ApiTransport> CreateTransport(CliArgs cli, CliConfig config, IConsoleLog log)
    {
        var (baseUrl, urlSource) = ResolveUrl(cli, config);
        var (token, tokenSource) = ResolveToken(cli, config);

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            return new ResultProblem("Invalid API URL '{0}'.", baseUrl);

        log.Log($"api: {baseUrl} (from {urlSource}); token: {(token is null ? "none" : tokenSource)}");

        var http = new HttpClient { BaseAddress = uri };
        if (!string.IsNullOrEmpty(token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return Result.Success(new ApiTransport(http, log));
    }

    private static (string Url, string Source) ResolveUrl(CliArgs cli, CliConfig config)
    {
        if (cli.Option("api-url") is { Length: > 0 } flag)
            return (flag, "--api-url");
        if (Environment.GetEnvironmentVariable(ApiUrlEnvVar) is { Length: > 0 } env)
            return (env, ApiUrlEnvVar);
        if (config.ApiUrl is { Length: > 0 } file)
            return (file, "~/.pl");
        return (DefaultApiUrl, "default");
    }

    /// <summary>The resolved base URL the next request would use — for <c>pl login</c> to persist alongside the token.</summary>
    public static string ResolveApiUrl(CliArgs cli, CliConfig config) => ResolveUrl(cli, config).Url;

    private static (string? Token, string Source) ResolveToken(CliArgs cli, CliConfig config)
    {
        if (cli.Option("token") is { Length: > 0 } flag)
            return (flag, "--token");
        if (Environment.GetEnvironmentVariable(TokenEnvVar) is { Length: > 0 } env)
            return (env, TokenEnvVar);
        if (config.Auth?.AccessToken is { Length: > 0 } file)
            return (file, "~/.pl");
        return (null, "none");
    }
}
