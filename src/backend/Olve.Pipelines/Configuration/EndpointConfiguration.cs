using Olve.Pipelines.Distribution;
using Olve.Pipelines.Kubernetes.Api;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Configs;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Streaming;

namespace Olve.Pipelines.Configuration;

public static class EndpointConfiguration
{
    /// <summary>Every endpoint the service serves (the auth-table test maps exactly these).</summary>
    public static void MapEndpoints(this WebApplication app)
    {
        app.MapJson();
        app.MapApi();
        app.MapCliDownloadEndpoints();
        app.MapAttemptEndpoints();
        app.MapRunEndpoints();
        app.MapManifestEndpoints();
        app.MapPipelineBindingEndpoints();
        app.MapWebhookTriggerEndpoints();
        app.MapCallbackInputEndpoints();
        app.MapEventStreamEndpoints();
        app.MapFallbackToFile("index.html").AllowAnonymous();
    }
}
