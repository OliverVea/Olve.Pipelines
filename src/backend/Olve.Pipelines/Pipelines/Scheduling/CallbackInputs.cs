using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Shared.Persistence;
using Olve.Pipelines.Shared.Problems;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.Pipelines.Scheduling;

/// <summary>
/// The instance's key for callback URLs: random, created on first use and kept in storage
/// (<see cref="StorageKey"/>), so URLs stay valid across restarts. Singleton.
/// </summary>
public sealed class CallbackKeyStore(ISnapshotStore store, ILogger<CallbackKeyStore> logger)
{
    public const string StorageKey = "callback-key";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private byte[]? _key;

    public async Task<byte[]> GetAsync(CancellationToken ct)
    {
        if (_key is { } cached) return cached;
        await _gate.WaitAsync(ct);
        try
        {
            if (_key is { } loaded) return loaded;
            var key = await store.TryReadAsync(StorageKey, ct);
            if (key is not { Length: 32 })
            {
                key = RandomNumberGenerator.GetBytes(32);
                await store.WriteAsync(StorageKey, key, ct);
                logger.LogInformation("Created the instance's callback URL key");
            }
            return _key = key;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// One-per-(run, step, input) callback URLs: <c>{Webhooks:PublicBaseUrl}/api/hooks/inputs/{run}/{step}/{input}/{mac}</c>,
/// the mac an HMAC of the three under the instance key. A URL answers only while that step waits for that input.
/// </summary>
public sealed class CallbackInputUrls(CallbackKeyStore keys, WebhookOptions webhooks)
{
    /// <summary>The run's callback URL per <c>step/input</c>, for every callback input in the manifest; empty when the instance has no public base URL.</summary>
    public async Task<IReadOnlyDictionary<string, string>> ForRunAsync(Id<Run> runId, RunPlan plan, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(webhooks.PublicBaseUrl))
            return new Dictionary<string, string>();
        var key = await keys.GetAsync(ct);
        var baseUrl = webhooks.PublicBaseUrl.TrimEnd('/');
        var run = runId.Value.Value.ToString("N");
        return plan.Stages.SelectMany(s => s.Steps)
            .SelectMany(s => s.ExternalInputs
                .Where(i => i.Value.From.Any(f => f is ExternalSource.Callback))
                .Select(i => (Key: $"{s.Name}/{i.Key}", Url: $"{baseUrl}/api/hooks/inputs/{run}/{s.Name}/{i.Key}/{Mac(key, run, s.Name, i.Key)}")))
            .DistinctBy(x => x.Key)
            .ToDictionary(x => x.Key, x => x.Url);
    }

    public async Task<bool> VerifyAsync(Id<Run> runId, string step, string input, string mac, CancellationToken ct)
    {
        var expected = Encoding.ASCII.GetBytes(Mac(await keys.GetAsync(ct), runId.Value.Value.ToString("N"), step, input));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(mac));
    }

    private static string Mac(byte[] key, string run, string step, string input)
        => Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{run}\n{step}\n{input}")));
}

/// <summary><c>POST /api/hooks/inputs/{run}/{step}/{input}/{mac}</c>: another system answers (or declines) a callback input.</summary>
public sealed class CallbackInputHandler(CallbackInputUrls urls, RunService runs, ExternalInputService inputs, IHttpContextAccessor http)
    : Contract.IWebhookTriggersAnswerCallbackHandler
{
    private const int MaxBodyBytes = 64 * 1024;

    public async Task<Contract.WebhookTriggersAnswerCallbackResponse> HandleAsync(Contract.WebhookTriggersAnswerCallbackRequest request, CancellationToken cancellationToken)
    {
        var id = new Id<Run>(new Id(request.RunId));
        if (!await urls.VerifyAsync(id, request.Step, request.Input, request.Mac, cancellationToken) || !runs.TryGet(id, out var run))
            return new Contract.WebhookTriggersAnswerCallbackResponse.NotFound(ApiProblems.Of(new NotFoundProblem("No such callback input.")));
        var context = http.HttpContext;
        if (context?.Request.ContentLength > MaxBodyBytes)
            return new Contract.WebhookTriggersAnswerCallbackResponse.PayloadTooLarge();
        if (context?.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = MaxBodyBytes;

        JsonElement body;
        try
        {
            using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
            body = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new Contract.WebhookTriggersAnswerCallbackResponse.BadRequest(ApiProblems.Of(new ResultProblem("The body is not JSON.")));
        }
        catch (BadHttpRequestException)
        {
            return new Contract.WebhookTriggersAnswerCallbackResponse.PayloadTooLarge();
        }

        var by = new Answerer("callback", $"callback from {context?.Connection.RemoteIpAddress}");
        var result = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("$decline", out var reason)
            ? inputs.Decline(run.PipelineId, run.Number, request.Step, reason.ValueKind == JsonValueKind.String ? reason.GetString() : null, by)
            : inputs.Answer(run.PipelineId, run.Number, request.Step, request.Input, body, by);
        return result.TryPickProblems(out var problems)
            ? RunViews.Fail<Contract.WebhookTriggersAnswerCallbackResponse>(problems,
                p => new Contract.WebhookTriggersAnswerCallbackResponse.NotFound(p),
                p => new Contract.WebhookTriggersAnswerCallbackResponse.Conflict(p),
                p => new Contract.WebhookTriggersAnswerCallbackResponse.BadRequest(p))
            : new Contract.WebhookTriggersAnswerCallbackResponse.Accepted();
    }
}
