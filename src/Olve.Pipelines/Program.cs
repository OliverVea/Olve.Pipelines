using Olve.Pipelines.Building.Api;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Health;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Kubernetes.Api;
using Olve.Pipelines.PipelineBuilders;
using Olve.Pipelines.PipelineBuilders.Api;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Api;
using Olve.Pipelines.PipelineSources;
using Olve.Pipelines.PipelineSources.Api;
using Olve.Pipelines.Processing;
using Olve.Pipelines.Processing.Api;
using Olve.Pipelines.Sourcing.Api;

var builder = WebApplication.CreateSlimBuilder(args);

builder.ConfigureHost(args);
builder.ConfigureJson();
builder.ConfigureAuthentication();
builder.ConfigureTelemetry();
builder.ConfigureStorage();
builder.ConfigureKubernetes();
builder.Services.AddPipelineServices();

var app = builder.Build();

app.Use(async (context, next) =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    await next();
    var path = context.Request.Path;
    if (path.StartsWithSegments("/api"))
    {
        app.Logger.LogInformation("{Method} {Path} {StatusCode} {Elapsed}ms",
            context.Request.Method, path, context.Response.StatusCode, sw.ElapsedMilliseconds);
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapJson();
app.MapAuthentication();
app.MapHealthEndpoints();
app.MapPipelineEndpoints();
app.MapPipelineSourceEndpoints();
app.MapPipelineBuilderEndpoints();
app.MapProcessingEndpoints();
app.MapSecretEndpoints();
app.MapSourceBundleEndpoints();
app.MapArtifactBundleEndpoints();

app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

public partial class Program;
