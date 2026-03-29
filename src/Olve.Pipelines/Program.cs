using Olve.Pipelines.Configuration;
using Olve.Pipelines.Health;
using Olve.Pipelines.PipelineArtifacts.Api;
using Olve.Pipelines.PipelineBuilders.Api;
using Olve.Pipelines.Pipelines.Api;
using Olve.Pipelines.PipelineSources.Api;
using Olve.Pipelines.Processing.Api;

var builder = WebApplication.CreateSlimBuilder(args);

builder.ConfigureHost(args);
builder.ConfigureJson();
builder.ConfigureAuthentication();
builder.ConfigureTelemetry();
builder.ConfigureStorage();
builder.Services.AddPipelineServices();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapJson();
app.MapAuthentication();
app.MapHealthEndpoints();
app.MapPipelineEndpoints();
app.MapPipelineSourceEndpoints();
app.MapPipelineBuilderEndpoints();
app.MapPipelineArtifactEndpoints();
app.MapProcessingEndpoints();

app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

public partial class Program;
