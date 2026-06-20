using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Olve.Pipelines.Cli.Diagnostics;

namespace Olve.Pipelines.Cli.Api;

/// <summary>
/// Thin, AOT-safe HTTP layer over <see cref="HttpClient"/>: serializes via source-generated
/// <c>JsonTypeInfo</c> (no reflection), maps non-success statuses to <see cref="ResultProblem"/>
/// (401/403 → "run pl login"), and logs each request to stderr under <c>--verbose</c>.
/// </summary>
public sealed class ApiTransport(HttpClient http, IConsoleLog log)
{
    public HttpClient Http => http;

    public Task<Result<T>> GetAsync<T>(string path, JsonTypeInfo<T> typeInfo, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, path, body: null, typeInfo, ct);

    public Task<Result<T>> SendAsync<T>(
        HttpMethod method, string path, HttpContent? body, JsonTypeInfo<T> typeInfo, CancellationToken ct) =>
        SendCoreAsync(method, path, body, async (resp, c) =>
        {
            var stream = await resp.Content.ReadAsStreamAsync(c);
            var value = await JsonSerializer.DeserializeAsync(stream, typeInfo, c);
            return value is null
                ? new ResultProblem("Empty response body from {0}.", path)
                : Result.Success(value);
        }, ct);

    /// <summary>For endpoints with no response body we care about.</summary>
    public async Task<Result> SendAsync(HttpMethod method, string path, HttpContent? body, CancellationToken ct)
    {
        var result = await SendCoreAsync(method, path, body,
            (_, _) => Task.FromResult(Result.Success(true)), ct);
        return result.TryPickProblems(out var problems) ? problems : Result.Success();
    }

    /// <summary>For endpoints returning a raw (untyped) body, e.g. health/ready/frontend-config.</summary>
    public Task<Result<string>> GetStringAsync(string path, CancellationToken ct) =>
        SendCoreAsync(HttpMethod.Get, path, body: null,
            async (resp, c) => Result.Success(await resp.Content.ReadAsStringAsync(c)), ct);

    public static HttpContent JsonBody<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        var json = JsonSerializer.Serialize(value, typeInfo);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private async Task<Result<T>> SendCoreAsync<T>(
        HttpMethod method, string path, HttpContent? body,
        Func<HttpResponseMessage, CancellationToken, Task<Result<T>>> onSuccess, CancellationToken ct)
    {
        log.Log($"{method.Method} {path}");
        try
        {
            using var request = new HttpRequestMessage(method, path) { Content = body };
            using var response = await http.SendAsync(request, ct);
            log.Log($"-> {(int)response.StatusCode} {response.ReasonPhrase}");

            return response.IsSuccessStatusCode
                ? await onSuccess(response, ct)
                : await ErrorAsync<T>(response, ct);
        }
        catch (HttpRequestException ex)
        {
            return new ResultProblem("Request to {0} failed: {1}", path, ex.Message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ResultProblem("Request to {0} timed out.", path);
        }
    }

    private static async Task<Result<T>> ErrorAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var code = (int)response.StatusCode;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new ResultProblem("Not authenticated ({0}). Run `pl login`.", code);

        var body = await SafeReadAsync(response, ct);
        var detail = string.IsNullOrWhiteSpace(body) ? string.Empty : $" — {Snippet(body)}";
        return new ResultProblem("Request failed: {0} {1}{2}", code, response.ReasonPhrase ?? string.Empty, detail);
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct); }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { return string.Empty; }
    }

    private static string Snippet(string body)
    {
        var trimmed = body.Trim();
        return trimmed.Length <= 400 ? trimmed : trimmed[..400] + "…";
    }
}
