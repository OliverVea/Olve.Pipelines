namespace Olve.Pipelines.Configuration;

public static class FrontendConfigEndpoints
{
    /// <param name="OidcScopes">Space-separated scopes to request; <c>groups</c> only when <c>Auth:Allow:Orgs</c> needs the claim.</param>
    public record FrontendConfig(string? OidcAuthority, string? OidcClientId, string OidcScopes);

    public static string Scopes(AuthAllowList allow) =>
        allow.Orgs.Count > 0 ? "openid profile email groups" : "openid profile email";

    public static void MapFrontendConfigEndpoints(this WebApplication app)
    {
        app.MapGet("/api/config/frontend", (IConfiguration config) =>
        {
            var authority = config["Auth:FrontendAuthority"];
            var clientId = config["Auth:FrontendAudience"];
            return Microsoft.AspNetCore.Http.Results.Ok(new FrontendConfig(authority, clientId, Scopes(AuthAllowList.From(config))));
        })
        .WithName("GetFrontendConfig")
        .WithTags("Instance")
        .WithSummary("Get the login settings")
        .WithDescription("The OIDC authority, client id and scopes the web UI and `pl login` use.")
        .AllowAnonymous();
    }
}
