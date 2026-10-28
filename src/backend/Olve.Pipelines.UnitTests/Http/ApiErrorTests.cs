using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Olve.Pipelines.UnitTests.Http;

/// <summary>The error model end to end over HTTP: status, <see cref="ApiProblem"/> body and logging.</summary>
public class ApiErrorTests
{
    private static async Task<(HttpResponseMessage Response, string Body, JsonElement Json)> Send(ApiErrorHost host, HttpMethod method, string path, string? json = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return (response, body, body.Length > 0 ? JsonDocument.Parse(body).RootElement.Clone() : default);
    }

    private static string Code(JsonElement json, int i = 0) => json.GetProperty("errors")[i].GetProperty("code").GetString()!;

    // Catches: a NotFoundProblem wrapped with plain context (Prepend) falling back to 400.
    [Test]
    public async Task NotFound_WithPrependedContext_Is404()
    {
        await using var host = await ApiErrorHost.StartAsync();
        var (response, _, json) = await Send(host, HttpMethod.Get, "/api/t/missing");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("application/json");
        await Assert.That(json.GetProperty("status").GetInt32()).IsEqualTo(404);
        await Assert.That(json.GetProperty("type").GetString()).IsEqualTo("urn:olve-pipelines:not-found");
        await Assert.That(json.GetProperty("detail").GetString()).IsEqualTo("Run #7 not found.");
        await Assert.That(json.GetProperty("errors").GetArrayLength()).IsEqualTo(2);
        await Assert.That(Code(json, 0)).IsEqualTo("invalid");
        await Assert.That(Code(json, 1)).IsEqualTo("not-found");
    }

    // Catches: conflicts answered as 400, or the conflicting field dropped from the body.
    [Test]
    public async Task Conflict_Is409_WithField()
    {
        await using var host = await ApiErrorHost.StartAsync();
        var (response, _, json) = await Send(host, HttpMethod.Post, "/api/t/conflict");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        var error = json.GetProperty("errors")[0];
        await Assert.That(error.GetProperty("code").GetString()).IsEqualTo("conflict");
        await Assert.That(error.GetProperty("field").GetString()).IsEqualTo("input.sha");
        await Assert.That(error.GetProperty("message").GetString()).IsEqualTo("Field 'sha' cannot change: completed step(s) build consumed it.");
    }

    // Catches: JSON-schema errors losing their field path, or repeating it inside the message.
    [Test]
    public async Task SchemaViolation_Is400_WithFieldPath()
    {
        await using var host = await ApiErrorHost.StartAsync();
        var (response, _, json) = await Send(host, HttpMethod.Post, "/api/t/invalid");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var error = json.GetProperty("errors")[0];
        await Assert.That(error.GetProperty("code").GetString()).IsEqualTo("invalid");
        await Assert.That(error.GetProperty("field").GetString()).IsEqualTo("input.sha");
        await Assert.That(error.GetProperty("message").GetString()).IsEqualTo("must match ^[0-9a-f]{7,40}$");
    }

    // Catches: framework binding failures (bad route/query value, malformed body) becoming 500s or empty bodies.
    [Test]
    [Arguments("GET", "/api/t/number/3", null)]
    [Arguments("POST", "/api/t/body", "{not json")]
    public async Task BindingFailure_Is400_WithBody(string method, string path, string? body)
    {
        await using var host = await ApiErrorHost.StartAsync();
        var (response, _, json) = await Send(host, new HttpMethod(method), path, body);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(Code(json)).IsEqualTo("invalid");
    }

    // Catches: an UnavailableProblem mapped to 400, and its exception text leaking into the body.
    [Test]
    public async Task Unavailable_Is503_WithoutExceptionText()
    {
        await using var host = await ApiErrorHost.StartAsync();
        var (response, body, json) = await Send(host, HttpMethod.Get, "/api/t/unavailable");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(Code(json)).IsEqualTo("unavailable");
        await Assert.That(body).DoesNotContain(ApiErrorHost.Secret);
    }

    // Catches: a transient exception answered as 500, or without Retry-After.
    [Test]
    public async Task TransientException_Is503_WithRetryAfter()
    {
        await using var host = await ApiErrorHost.StartAsync();
        var (response, body, json) = await Send(host, HttpMethod.Get, "/api/t/throw-transient");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(response.Headers.RetryAfter).IsNotNull();
        await Assert.That(Code(json)).IsEqualTo("unavailable");
        await Assert.That(body).DoesNotContain(ApiErrorHost.Secret);
    }

    // Catches: unhandled exceptions leaking their message/type/stack, or a body in another shape.
    [Test]
    public async Task UnhandledException_Is500_GenericBody()
    {
        await using var host = await ApiErrorHost.StartAsync();
        var (response, body, json) = await Send(host, HttpMethod.Get, "/api/t/throw");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(Code(json)).IsEqualTo("internal");
        await Assert.That(body).DoesNotContain(ApiErrorHost.Secret);
        await Assert.That(body).DoesNotContain(nameof(InvalidOperationException));
        await Assert.That(body).DoesNotContain("   at ");
    }

    // Catches: severity/source/exceptionSummary/tags (ResultProblem internals) serialized into bodies.
    [Test]
    [Arguments("/api/t/missing")]
    [Arguments("/api/t/unavailable")]
    public async Task Body_HasOnlyTheApiProblemShape(string path)
    {
        await using var host = await ApiErrorHost.StartAsync();
        var (_, _, json) = await Send(host, HttpMethod.Get, path);

        var names = json.EnumerateObject().Select(p => p.Name).ToArray();
        await Assert.That(names).IsEquivalentTo(["type", "title", "status", "detail", "errors", "traceId"]);
        foreach (var error in json.GetProperty("errors").EnumerateArray())
            await Assert.That(error.EnumerateObject().All(p => p.Name is "code" or "message" or "field")).IsTrue();
    }

    // Catches: the mapping and the middleware both logging one failure, or the exception not logged with its stack.
    [Test]
    public async Task Failure_IsLoggedExactlyOnce()
    {
        await using var host = await ApiErrorHost.StartAsync();
        await Send(host, HttpMethod.Get, "/api/t/throw");
        await Send(host, HttpMethod.Get, "/api/t/unavailable");

        var thrown = host.Logs.Entries.Where(e => e.Exception?.Message == ApiErrorHost.Secret || e.Message.Contains(ApiErrorHost.Secret)).ToArray();
        await Assert.That(thrown.Length).IsEqualTo(2); // one per request
        await Assert.That(thrown.Count(e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException)).IsEqualTo(1);
        await Assert.That(thrown.Count(e => e.Message.Contains("Kubernetes is unreachable."))).IsEqualTo(1);
    }

    // Catches: a body traceId that can't be found in the request's log lines.
    [Test]
    public async Task TraceId_MatchesTheLogScope()
    {
        await using var host = await ApiErrorHost.StartAsync();
        var (_, _, json) = await Send(host, HttpMethod.Get, "/api/t/throw");
        var traceId = json.GetProperty("traceId").GetString();

        var logged = host.Logs.Entries.Single(e => e.Exception?.Message == ApiErrorHost.Secret);
        await Assert.That(traceId).IsNotNull();
        await Assert.That(logged.Scope.Values).Contains(traceId);
    }

    // Catches: empty-bodied deletion 404s (the old WithDeletionMapping) slipping past the shape.
    [Test]
    public async Task DeletionNotFound_Is404_WithBody()
    {
        await using var host = await ApiErrorHost.StartAsync();
        var (response, _, json) = await Send(host, HttpMethod.Delete, "/api/t/gone");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(Code(json)).IsEqualTo("not-found");
    }

    // Catches: framework-produced empty errors (e.g. 405) under /api not getting the shape.
    [Test]
    public async Task EmptyFrameworkError_GetsTheShape()
    {
        await using var host = await ApiErrorHost.StartAsync();
        var (response, _, json) = await Send(host, HttpMethod.Put, "/api/t/ok");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.MethodNotAllowed);
        await Assert.That(json.GetProperty("status").GetInt32()).IsEqualTo(405);
        await Assert.That(Code(json)).IsEqualTo("invalid");
    }

    // Catches: the mapping filter breaking successful results.
    [Test]
    public async Task Success_Is200WithValue()
    {
        await using var host = await ApiErrorHost.StartAsync();
        var (response, body, _) = await Send(host, HttpMethod.Get, "/api/t/ok");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body).IsEqualTo("\"fine\"");
    }
}
