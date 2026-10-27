using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Streaming;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests.Streaming;

/// <summary>The SSE endpoints over a real Kestrel socket (no hosted services, no storage).</summary>
public class EventStreamEndpointTests
{
    private static async Task<(WebApplication App, HttpClient Http)> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddPipelineServices();
        builder.Services.RemoveAll<IHostedService>();
        builder.Services.RemoveAll<IHostedLifecycleService>();
        builder.Services.AddSingleton<IAmazonS3>(new AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config { ServiceURL = "http://127.0.0.1:1" }));
        builder.Services.AddSingleton(new StorageOptions("unused"));
        builder.Configuration["Auth:Disable"] = "true";
        builder.ConfigureAuthentication();
        var app = builder.Build();
        app.MapAuthentication();
        app.MapEventStreamEndpoints();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(20) });
    }

    private static async Task<List<string>> ReadEventAsync(StreamReader reader)
    {
        var lines = new List<string>();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length == 0) break;
            lines.Add(line);
        }
        return lines;
    }

    [Test]
    public async Task Events_StreamsHelloThenPublishedChanges_Unbuffered()
    {
        var (app, http) = await StartAsync();
        await using var _ = app;
        using var __ = http;
        var pipelineId = Id.New<Pipeline>();

        using var response = await http.GetAsync($"/api/events?pipeline={pipelineId.Value.Value}", HttpCompletionOption.ResponseHeadersRead);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("text/event-stream");
        await Assert.That(response.Headers.GetValues("X-Accel-Buffering").Single()).IsEqualTo("no");
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

        var hello = await ReadEventAsync(reader);
        await Assert.That(hello).Contains("event: hello");
        await Assert.That(hello.Any(l => l.StartsWith("retry: "))).IsTrue();

        var broadcaster = app.Services.GetRequiredService<ChangeBroadcaster>();
        broadcaster.Publish(ChangeEvent.Pipeline(Id.New<Pipeline>(), "added"));
        broadcaster.Publish(ChangeEvent.BlockedSteps(pipelineId, ["deploy"]));

        var blocked = await ReadEventAsync(reader);
        await Assert.That(blocked).Contains("event: blocked");
        await Assert.That(blocked).Contains($$"""data: {"pipelineId":"{{pipelineId.Value.Value}}","blockedSteps":["deploy"]}""");
        await Assert.That(blocked.Any(l => l.StartsWith("id: ") && l.EndsWith("-2"))).IsTrue();
    }

    [Test]
    public async Task LogStream_UnknownRun_Is404()
    {
        var (app, http) = await StartAsync();
        await using var _ = app;
        using var __ = http;

        using var response = await http.GetAsync($"/api/pipelines/{Guid.NewGuid()}/runs/1/steps/build/logs/stream");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }
}
