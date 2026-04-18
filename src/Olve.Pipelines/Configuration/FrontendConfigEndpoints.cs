namespace Olve.Pipelines.Configuration;

public static class FrontendConfigEndpoints
{
    public record FrontendConfig(string? OidcAuthority, string? OidcClientId);

    public static void MapFrontendConfigEndpoints(this WebApplication app)
    {
        app.MapGet("/api/config/frontend", (IConfiguration config) =>
        {
            var authority = config["Auth:FrontendAuthority"];
            var clientId = config["Auth:FrontendAudience"];
            return Microsoft.AspNetCore.Http.Results.Ok(new FrontendConfig(authority, clientId));
        })
        .WithName("GetFrontendConfig")
        .AllowAnonymous();
    }
}
