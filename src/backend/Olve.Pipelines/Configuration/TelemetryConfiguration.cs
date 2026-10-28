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
        // Unset or empty (OpenTelemetry__Endpoint="") = no exporter.
        if (string.IsNullOrWhiteSpace(endpoint)) return;

        var serviceName = builder.Configuration["Telemetry:ServiceName"] ?? "olve-pipelines";
        var protocol = builder.Configuration["OpenTelemetry:Protocol"];

        // OTLP header list ("k1=v1,k2=v2", as OTEL_EXPORTER_OTLP_HEADERS), e.g. a backend's API key.
        var headers = builder.Configuration["OpenTelemetry:Headers"];

        var baseEndpoint = endpoint.TrimEnd('/');

        void ConfigureOtlp(OtlpExporterOptions options, string signalPath)
        {
            options.Endpoint = new Uri($"{baseEndpoint}{signalPath}");

            if (string.Equals(protocol, "http", StringComparison.OrdinalIgnoreCase))
            {
                options.Protocol = OtlpExportProtocol.HttpProtobuf;
            }

            if (!string.IsNullOrWhiteSpace(headers))
            {
                options.Headers = headers;
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
