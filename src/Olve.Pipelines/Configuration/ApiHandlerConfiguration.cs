using Olve.Pipelines.Api;
using Olve.Pipelines.Health;

namespace Olve.Pipelines.Configuration;

public static class ApiHandlerConfiguration
{
    /// <summary>One handler per contract operation (<c>spec/</c>); <c>UsePipelinesApi</c> refuses to start without all of them.</summary>
    public static void AddApiHandlers(this IServiceCollection services)
    {
        services.AddSingleton<IInstanceHealthHandler, HealthHandler>();
        services.AddSingleton<IInstanceReadyHandler, ReadinessHandler>();
        services.AddSingleton<IInstanceFrontendConfigHandler, FrontendConfigHandler>();
    }

    /// <summary>
    /// Maps the contract's operations and applies what the contract doesn't carry yet: the auth
    /// policy and the OpenAPI tag and summary of each (api.json feeds the API reference until it
    /// is generated from the spec, docs/design/spec-first.md).
    /// </summary>
    public static void MapApi(this WebApplication app)
    {
        var api = app.UsePipelinesApi();
        api.InstanceHealth.AllowAnonymous().WithTags("Instance").WithSummary("Liveness");
        api.InstanceReady.AllowAnonymous().WithTags("Instance").WithSummary("Readiness");
        api.InstanceFrontendConfig.AllowAnonymous().WithTags("Instance").WithSummary("Get the login settings");
    }
}
