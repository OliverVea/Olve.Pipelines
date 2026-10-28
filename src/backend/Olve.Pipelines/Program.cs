using Olve.Pipelines.Api;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Kubernetes;

var builder = WebApplication.CreateSlimBuilder(args);

builder.ConfigureHost(args);
builder.ConfigureLogging();
builder.ConfigureJson();
builder.ConfigureAuthentication();
builder.ConfigureTelemetry();
builder.ConfigureStorage();
builder.ConfigureKubernetes();
builder.Services.AddPipelineServices();
builder.Services.AddApiErrors();
builder.Services.AddPipelinesApi();
builder.Services.AddApiHandlers();

var app = builder.Build();

// Store events isolate their handlers: a throwing handler no longer fails the write that raised the
// event, and its exception goes here instead of being swallowed.
var eventLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Olve.Pipelines.StoreEvents");
EventDispatch.OnHandlerException = ex =>
    eventLogger.LogError(ex, "Unhandled exception in a store event handler; other handlers still ran");

app.Use(async (context, next) =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        await next();
    }
    finally
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/api"))
        {
            // Who did it: authentication has run by now, so the user is known.
            var user = context.User.FindFirst("preferred_username")?.Value
                ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "anonymous";
            app.Logger.LogInformation("{Method} {Path} {StatusCode} {Elapsed}ms by {User}",
                context.Request.Method, path, context.Response.StatusCode, sw.ElapsedMilliseconds, user);
        }
    }
});
app.UseApiErrors();

app.UseDefaultFiles();

// Serve raw Markdown (the /docs setup guide) with the right content-type. .md is an unknown
// extension to the static-file middleware, so without this it would 404 here and fall through
// to the SPA fallback below — handing back index.html instead of the doc. Agents fetch these
// at /docs/<page>.md and parse the raw text.
var contentTypeProvider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
contentTypeProvider.Mappings[".md"] = "text/markdown; charset=utf-8";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypeProvider });
app.MapAuthentication();
app.MapEndpoints();

app.Run();

public partial class Program;
