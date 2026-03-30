using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Olve.Pipelines.Configuration;

public static class TelemetryConfiguration
{
    public static void ConfigureTelemetry(this WebApplicationBuilder builder)
    {
        var endpoint = builder.Configuration["OpenTelemetry:Endpoint"];
        if (endpoint is null) return;

        var serviceName = builder.Configuration["Telemetry:ServiceName"] ?? "olve-pipelines";
        var protocol = builder.Configuration["OpenTelemetry:Protocol"];

        var tokenUrl = builder.Configuration["OpenTelemetry:OAuth2:TokenUrl"];
        var clientId = builder.Configuration["OpenTelemetry:OAuth2:ClientId"];
        var clientSecret = builder.Configuration["OpenTelemetry:OAuth2:ClientSecret"];
        var scope = builder.Configuration["OpenTelemetry:OAuth2:Scope"];

        ICredentialsProvider<OTelCredentials>? credentialsProvider = null;
        if (tokenUrl is not null && clientId is not null && clientSecret is not null)
        {
            var tokenProvider = new OAuth2TokenProvider(tokenUrl, clientId, clientSecret, scope);
            credentialsProvider = new OTelCredentialsProvider(tokenProvider);
        }

        var baseEndpoint = endpoint.TrimEnd('/');

        void ConfigureOtlp(OtlpExporterOptions options, string signalPath)
        {
            options.Endpoint = new Uri($"{baseEndpoint}{signalPath}");

            if (string.Equals(protocol, "http", StringComparison.OrdinalIgnoreCase))
            {
                options.Protocol = OtlpExportProtocol.HttpProtobuf;
            }

            if (credentialsProvider is not null)
            {
                var creds = credentialsProvider.GetCredentialsAsync().GetAwaiter().GetResult();
                options.Headers = $"Authorization=Bearer {creds.BearerToken}";
            }
        }

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddOtlpExporter(o => ConfigureOtlp(o, "/v1/traces")))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddOtlpExporter(o => ConfigureOtlp(o, "/v1/metrics")));

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.AddOtlpExporter(o => ConfigureOtlp(o, "/v1/logs"));
            logging.IncludeFormattedMessage = true;
        });
    }
}
