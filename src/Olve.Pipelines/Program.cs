using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Distribution;
using Olve.Pipelines.Health;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Kubernetes.Api;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Triggers;

var builder = WebApplication.CreateSlimBuilder(args);

builder.ConfigureHost(args);
builder.ConfigureLogging();
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

// Serve raw Markdown (the /docs setup guide) with the right content-type. .md is an unknown
// extension to the static-file middleware, so without this it would 404 here and fall through
// to the SPA fallback below — handing back index.html instead of the doc. Agents fetch these
// at /docs/<page>.md and parse the raw text.
var contentTypeProvider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
contentTypeProvider.Mappings[".md"] = "text/markdown; charset=utf-8";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypeProvider });
app.MapJson();
app.MapAuthentication();
app.MapHealthEndpoints();
app.MapFrontendConfigEndpoints();
app.MapCliDownloadEndpoints();
app.MapPipelineEndpoints();
app.MapProductionStepEndpoints();
app.MapProcessingStepEndpoints();
app.MapSecretEndpoints();
app.MapArtifactBundleEndpoints();
app.MapJobEndpoints();
app.MapTriggerEndpoints();
app.MapPipelineDocumentEndpoints();
app.MapPipelineBindingEndpoints();
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

public partial class Program;
