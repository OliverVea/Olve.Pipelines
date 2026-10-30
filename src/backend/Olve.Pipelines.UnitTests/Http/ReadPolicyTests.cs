using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Olve.Pipelines.Configuration;

namespace Olve.Pipelines.UnitTests.Http;

public class ReadPolicyTests
{
    private static async Task<HttpStatusCode> AnonymousGet()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["Auth:Audience"] = "olve-pipelines";
        builder.Configuration["Auth:SigningKey"] = new string('k', 64);
        builder.ConfigureAuthentication();

        await using var app = builder.Build();
        app.MapAuthentication();
        app.MapGet("/api/read", () => "ok").RequireRead();
        await app.StartAsync();

        return (await app.GetTestClient().GetAsync("/api/read")).StatusCode;
    }

    [Test]
    public async Task Reads_NeedASignedInUser() =>
        await Assert.That(await AnonymousGet()).IsEqualTo(HttpStatusCode.Unauthorized);
}
