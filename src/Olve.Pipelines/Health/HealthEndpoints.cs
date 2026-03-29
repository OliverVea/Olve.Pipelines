namespace Olve.Pipelines.Health;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/api/health", () => Microsoft.AspNetCore.Http.Results.Ok())
            .AllowAnonymous();
    }
}
