using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Olve.Pipelines.Api;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Kubernetes;

namespace Olve.Pipelines.UnitTests.Http;

/// <summary>
/// The auth table of every endpoint the service maps. Adding an endpoint to either list is a deliberate
/// change: anonymous endpoints are public. Everything else needs a signed-in, allow-listed user.
/// </summary>
public class EndpointAuthTests
{
    private static readonly string[] Anonymous =
    [
        "GET /api/config/frontend",
        "GET /api/health",
        "GET /api/ready",
        "GET /download/{**asset}",
        "GET /install.ps1",
        "GET /install.sh",
        WebUi,
        "HEAD /download/{**asset}",
        "POST /api/hooks/inputs/{runId}/{step}/{input}/{mac}",
        "POST /api/hooks/{pipelineId}/{trigger}",
        "POST /api/webhooks/binding/{bindingId}/github",
    ];

    private static readonly string[] Reads =
    [
        "GET /api/attempts",
        "GET /api/events",
        "GET /api/pipelines",
        "GET /api/pipelines/{pipelineId}",
        "GET /api/pipelines/{pipelineId}/binding",
        "GET /api/pipelines/{pipelineId}/binding/status",
        "GET /api/pipelines/{pipelineId}/blocked-steps",
        "GET /api/pipelines/{pipelineId}/config",
        "GET /api/pipelines/{pipelineId}/config/check",
        "GET /api/pipelines/{pipelineId}/config/history",
        "GET /api/pipelines/{pipelineId}/health",
        "GET /api/pipelines/{pipelineId}/runs",
        "GET /api/pipelines/{pipelineId}/runs/critical-path",
        "GET /api/pipelines/{pipelineId}/runs/{number}",
        "GET /api/pipelines/{pipelineId}/runs/{number}/attempts",
        "GET /api/pipelines/{pipelineId}/runs/{number}/steps/{step}/attempts",
        "GET /api/pipelines/{pipelineId}/runs/{number}/steps/{step}/logs",
        "GET /api/pipelines/{pipelineId}/runs/{number}/steps/{step}/logs/stream",
        "GET /api/pipelines/{pipelineId}/runs/{number}/steps/{step}/output",
        "GET /api/pipelines/{pipelineId}/steps/{step}/history",
        "GET /api/pipelines/{pipelineId}/versions",
        "GET /api/pipelines/{pipelineId}/versions/{number}",
        "GET /api/pipelines/{pipelineId}/versions/{number}/effective",
    ];

    private const string WebUi = "GET {*path:nonfile}";

    private static async Task<(IReadOnlyList<(string Endpoint, bool Anonymous, string[] Policies, bool InContract)> Table, AuthorizationOptions Options)> MapAll()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["Auth:Audience"] = "olve-pipelines";
        builder.Configuration["Auth:SigningKey"] = new string('k', 64);
        builder.Configuration["Storage:Mode"] = "Ephemeral";
        builder.ConfigureJson();
        builder.ConfigureAuthentication();
        builder.ConfigureStorage();
        builder.ConfigureKubernetes();
        builder.Services.AddPipelineServices();
        builder.Services.AddApiErrors();
        builder.Services.AddPipelinesApi();
        builder.Services.AddApiHandlers();

        await using var app = builder.Build();
        app.MapAuthentication();
        app.MapEndpoints();

        var table = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => (
                Endpoint: $"{e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "*"} {e.RoutePattern.RawText}",
                Anonymous: e.Metadata.GetMetadata<IAllowAnonymous>() is not null,
                Policies: e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy ?? "").ToArray(),
                InContract: e.Metadata.GetMetadata<Olve.TypeSpec.ApiOperation>() is not null))
            .ToList();
        return (table, app.Services.GetRequiredService<IOptions<AuthorizationOptions>>().Value);
    }

    // Catches: a hand-written Map* endpoint; every endpoint but the web UI is a contract operation (src/spec/).
    [Test]
    public async Task EveryEndpointButTheWebUi_IsInTheContract()
    {
        var (table, _) = await MapAll();

        await Assert.That(table.Where(e => !e.InContract).Select(e => e.Endpoint)).IsEquivalentTo([WebUi]);
    }

    // Catches: a stray .AllowAnonymous() on a mutation or on secrets.
    [Test]
    public async Task OnlyTheseEndpointsAreAnonymous()
    {
        var (table, _) = await MapAll();

        await Assert.That(table.Where(e => e.Anonymous).Select(e => e.Endpoint)).IsEquivalentTo(Anonymous);
    }

    // Catches: .RequireRead() on secrets, step files or a mutation.
    [Test]
    public async Task OnlyTheseEndpointsUseTheReadPolicy()
    {
        var (table, _) = await MapAll();

        await Assert.That(table.Where(e => e.Policies.Contains(AuthenticationConfiguration.ReadPolicy)).Select(e => e.Endpoint))
            .IsEquivalentTo(Reads);
    }

    [Test]
    public async Task EverythingElse_FallsBackToASignedInUser()
    {
        var (table, options) = await MapAll();

        await Assert.That(options.FallbackPolicy).IsNotNull();
        await Assert.That(table.Where(e => !e.Anonymous && e.Policies.Any(p => p != "" && p != AuthenticationConfiguration.ReadPolicy)))
            .IsEmpty();
    }
}
