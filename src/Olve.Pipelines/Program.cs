using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Health;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Kubernetes.Api;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Pipelines.Triggers;

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
app.MapProductionStepEndpoints();
app.MapProcessingStepEndpoints();
app.MapSecretEndpoints();
app.MapArtifactBundleEndpoints();
app.MapJobEndpoints();
app.MapTriggerEndpoints();
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

public partial class Program;
