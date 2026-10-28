using Olve.Pipelines.Api;
using Olve.Pipelines.Health;
using Olve.Pipelines.Pipelines;

namespace Olve.Pipelines.Configuration;

public static class ApiHandlerConfiguration
{
    /// <summary>One handler per contract operation (<c>src/spec/</c>); <c>UsePipelinesApi</c> refuses to start without all of them.</summary>
    public static void AddApiHandlers(this IServiceCollection services)
    {
        services.AddSingleton<IInstanceHealthHandler, HealthHandler>();
        services.AddSingleton<IInstanceReadyHandler, ReadinessHandler>();
        services.AddSingleton<IInstanceFrontendConfigHandler, FrontendConfigHandler>();
        services.AddTransient<IPipelinesListHandler, ListPipelinesHandler>();
        services.AddTransient<IPipelinesSummariesHandler, ListPipelineSummariesHandler>();
        services.AddTransient<IPipelinesGetHandler, GetPipelineHandler>();
        services.AddTransient<IPipelinesDeleteHandler, DeletePipelineHandler>();
        services.AddTransient<IPipelinesHealthHandler, GetPipelineHealthHandler>();
    }

    /// <summary>
    /// Maps the contract's operations and applies what the contract doesn't carry yet: the auth
    /// policy and the OpenAPI tag and summary of each (api.json feeds the API reference until it
    /// is generated from the spec, docs/design/spec-first.md). Writes need a signed-in user (the
    /// fallback policy); reads the read policy.
    /// </summary>
    public static void MapApi(this WebApplication app)
    {
        var api = app.UsePipelinesApi();
        api.InstanceHealth.AllowAnonymous().WithTags("Instance").WithSummary("Liveness");
        api.InstanceReady.AllowAnonymous().WithTags("Instance").WithSummary("Readiness");
        api.InstanceFrontendConfig.AllowAnonymous().WithTags("Instance").WithSummary("Get the login settings");
        api.PipelinesList.RequireRead().WithTags("Pipelines").WithSummary("List pipelines");
        api.PipelinesSummaries.RequireRead().WithTags("Pipelines").WithSummary("List pipeline cards");
        api.PipelinesGet.RequireRead().WithTags("Pipelines").WithSummary("Get a pipeline");
        api.PipelinesDelete.WithTags("Pipelines").WithSummary("Delete a pipeline");
        api.PipelinesHealth.RequireRead().WithTags("Pipelines").WithSummary("Get a pipeline's health");
    }
}
