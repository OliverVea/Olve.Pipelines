namespace Olve.Pipelines.Configuration;

public static class EndpointConfiguration
{
    /// <summary>Every endpoint the service serves: the contract's operations and the web UI.</summary>
    public static void MapEndpoints(this WebApplication app)
    {
        app.MapApi();
        app.MapFallbackToFile("index.html", StaticCaching.Options()).AllowAnonymous();
    }
}
