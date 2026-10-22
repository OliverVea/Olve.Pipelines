using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Olve.Pipelines.Cli.Api.Contracts;
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

    /// <summary>Streams a (possibly binary) response body into <paramref name="destination"/>; returns the bytes copied.</summary>
    public Task<Result<long>> DownloadAsync(string path, Stream destination, CancellationToken ct) =>
        SendCoreAsync(HttpMethod.Get, path, body: null, async (resp, c) =>
        {
            // Copy inside the callback: SendCoreAsync disposes the response afterwards.
            await using var source = await resp.Content.ReadAsStreamAsync(c);
            var before = destination.CanSeek ? destination.Position : 0;
            await source.CopyToAsync(destination, c);
            return Result.Success(destination.CanSeek ? destination.Position - before : -1L);
        }, ct, HttpCompletionOption.ResponseHeadersRead);

    /// <summary>Reads a server-sent event stream, calling <paramref name="onEvent"/>(type, data) per event, until it ends or <paramref name="ct"/> fires.</summary>
    public async Task<Result> ReadEventsAsync(string path, Func<string, string, Task> onEvent, CancellationToken ct)
    {
        var result = await SendCoreAsync(HttpMethod.Get, path, body: null, async (resp, c) =>
        {
            using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(c));
            var type = "message";
            var data = new StringBuilder();
            try
            {
                while (await reader.ReadLineAsync(c) is { } line)
                {
                    if (line.Length == 0)
                    {
                        await onEvent(type, data.ToString());
                        (type, _) = ("message", data.Clear());
                    }
                    else if (line.StartsWith("event:", StringComparison.Ordinal))
                        type = line[6..].Trim();
                    else if (line.StartsWith("data:", StringComparison.Ordinal))
                        data.Append(data.Length > 0 ? "\n" : "").Append(line[5..].TrimStart(' '));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException)
            {
            }
            return Result.Success(true);
        }, ct, HttpCompletionOption.ResponseHeadersRead);
        return result.TryPickProblems(out var problems) ? problems : Result.Success();
    }

    public static HttpContent JsonBody<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        var json = JsonSerializer.Serialize(value, typeInfo);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    /// <summary>A body of already-serialized JSON text, sent verbatim (e.g. a pipeline input or a manifest).</summary>
    public static HttpContent RawJson(string json) => new StringContent(json, Encoding.UTF8, "application/json");

    private async Task<Result<T>> SendCoreAsync<T>(
        HttpMethod method, string path, HttpContent? body,
        Func<HttpResponseMessage, CancellationToken, Task<Result<T>>> onSuccess, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        log.Log($"{method.Method} {path}");
        try
        {
            using var request = new HttpRequestMessage(method, path) { Content = body };
            using var response = await http.SendAsync(request, completion, ct);
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

        // The API reports request errors as a ResultProblem[]: one `error:` line per problem.
        if (ParseProblems(body) is { Length: > 0 } serverProblems)
            return new ResultProblemCollection(serverProblems.Select(p => new ResultProblem("{0}", p.Message)));

        var detail = string.IsNullOrWhiteSpace(body) ? string.Empty : $" — {Snippet(body)}";
        return new ResultProblem("Request failed: {0} {1}{2}", code, response.ReasonPhrase ?? string.Empty, detail);
    }

    private static ServerProblem[]? ParseProblems(string body)
    {
        var trimmed = body.TrimStart();
        if (!trimmed.StartsWith('['))
            return null;
        try
        {
            var problems = JsonSerializer.Deserialize(trimmed, CliJsonContext.Default.ServerProblemArray);
            return problems?.Where(p => !string.IsNullOrEmpty(p.Message)).ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
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
