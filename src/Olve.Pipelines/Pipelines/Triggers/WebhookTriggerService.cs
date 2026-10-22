using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Sources;

namespace Olve.Pipelines.Pipelines.Triggers;

/// <summary>What happened to an inbound webhook trigger delivery.</summary>
public enum WebhookTriggerReceipt
{
    /// <summary>No such pipeline, no runnable version, or the version declares no webhook trigger of that name.</summary>
    NotFound,

    /// <summary>The trigger's secret could not be read.</summary>
    Unavailable,

    /// <summary>Verification failed.</summary>
    Unauthorized,

    /// <summary>Verified; mapping (and maybe a run start) continues in the background.</summary>
    Accepted,

    /// <summary>Verified, but too many deliveries are being mapped already; the sender should retry.</summary>
    Busy,
}

/// <summary>
/// The <c>webhook</c> triggers declared by each pipeline's current version: verify a delivery with the
/// trigger's secret (<see cref="WebhookVerification"/>), then map <c>{headers, body}</c> in the background
/// (a sender like GitHub waits only ~10 s) and start a Webhook run. Hosted only so shutdown cancels the
/// background mappings.
/// </summary>
public class WebhookTriggerService(IServiceProvider sp, ILogger<WebhookTriggerService> logger) : IHostedService, IDisposable
{
    internal const int MaxInFlight = 8;
    private static readonly TimeSpan SecretTtl = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SecretRecheckAfter = TimeSpan.FromSeconds(10);
    private const int RememberedDeliveries = 256;

    private readonly CancellationTokenSource _lifetime = new();
    private readonly TimeProvider _time = sp.GetService<TimeProvider>() ?? TimeProvider.System;

    // Unauthenticated requests reach this endpoint: a cached secret keeps them off the K8s API.
    private readonly ConcurrentDictionary<(Id<Pipeline>, string), (string Value, DateTimeOffset At)> _secrets = new();
    private readonly LinkedList<string> _deliveryOrder = new();
    private readonly HashSet<string> _deliveries = new(StringComparer.Ordinal);
    private int _inFlight;

    public async Task<WebhookTriggerReceipt> ReceiveAsync(Id<Pipeline> pipelineId, string triggerName, WebhookDelivery delivery, CancellationToken ct)
    {
        using var scope = sp.CreateScope();
        var services = scope.ServiceProvider;
        if (services.GetRequiredService<RunInputService>().GetTarget(pipelineId).TryPickProblems(out _, out var target)
            || target.Manifest.Triggers.OfType<WebhookTrigger>().FirstOrDefault(t => t.Name == triggerName) is not { } trigger)
            return WebhookTriggerReceipt.NotFound;

        var cacheKey = (pipelineId, trigger.Verify.Secret);
        var now = _time.GetUtcNow();
        var fresh = _secrets.TryGetValue(cacheKey, out var cached) && now - cached.At < SecretTtl;
        var verified = fresh && Verification(trigger.Verify, cached.Value).Verify(delivery);
        if (!verified && (!fresh || now - cached.At >= SecretRecheckAfter))
        {
            // Not cached, expired, or the secret may have just been rotated.
            var secret = await services.GetRequiredService<IPipelineSecretReader>().TryGetSecretAsync(pipelineId, trigger.Verify.Secret, ct);
            if (secret.TryPickProblems(out var secretProblems, out var key))
            {
                logger.LogProblems(LogLevel.Warning, secretProblems, "Webhook trigger '{TriggerName}' of pipeline '{PipelineId}' cannot read secret '{SecretName}'",
                    triggerName, pipelineId, trigger.Verify.Secret);
                return WebhookTriggerReceipt.Unavailable;
            }

            _secrets[cacheKey] = (key, now);
            verified = Verification(trigger.Verify, key).Verify(delivery);
        }

        if (!verified)
        {
            logger.LogWarning("Webhook trigger '{TriggerName}' of pipeline '{PipelineId}' rejected a delivery: verification failed", triggerName, pipelineId);
            return WebhookTriggerReceipt.Unauthorized;
        }

        if (delivery.Headers.TryGetValue("X-GitHub-Delivery", out var deliveryId) && !Remember($"{pipelineId}/{triggerName}/{deliveryId}"))
        {
            logger.LogInformation("Webhook trigger '{TriggerName}' of pipeline '{PipelineId}' ignored redelivery {DeliveryId}", triggerName, pipelineId, deliveryId);
            return WebhookTriggerReceipt.Accepted;
        }

        if (Interlocked.Increment(ref _inFlight) > MaxInFlight)
        {
            Interlocked.Decrement(ref _inFlight);
            logger.LogWarning("Webhook trigger '{TriggerName}' of pipeline '{PipelineId}' turned a delivery away: {MaxInFlight} already being mapped",
                triggerName, pipelineId, MaxInFlight);
            return WebhookTriggerReceipt.Busy;
        }

        var eventJson = EventJson(delivery, trigger.Verify);
        _ = Task.Run(async () =>
        {
            try
            {
                await MapAsync(pipelineId, triggerName, eventJson);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }, CancellationToken.None);
        return WebhookTriggerReceipt.Accepted;
    }

    /// <summary>False when <paramref name="id"/> was seen among the recent deliveries.</summary>
    private bool Remember(string id)
    {
        lock (_deliveries)
        {
            if (!_deliveries.Add(id))
                return false;
            _deliveryOrder.AddLast(id);
            if (_deliveryOrder.Count > RememberedDeliveries)
            {
                _deliveries.Remove(_deliveryOrder.First!.Value);
                _deliveryOrder.RemoveFirst();
            }
            return true;
        }
    }

    private async Task MapAsync(Id<Pipeline> pipelineId, string triggerName, string eventJson)
    {
        try
        {
            using var scope = sp.CreateScope();
            async Task StartAsync()
            {
                var outcome = await scope.ServiceProvider.GetRequiredService<TriggerMappingService>()
                    .MapAndStartAsync(pipelineId, triggerName, RunTriggerKind.Webhook, eventJson, _lifetime.Token);
                if (outcome.TryPickProblems(out var problems))
                    logger.LogProblems(LogLevel.Warning, problems, "Webhook trigger '{TriggerName}' of pipeline '{PipelineId}' did not start a run",
                        triggerName, pipelineId);
            }

            if (scope.ServiceProvider.GetService<IConfigLock>() is not { } configLock)
                await StartAsync();
            else if (!await configLock.RunIfConfigCurrentAsync(pipelineId, StartAsync, _lifetime.Token))
                logger.LogWarning("Webhook trigger '{TriggerName}' of pipeline '{PipelineId}' dropped a delivery: the pipeline's config commit is not applied",
                    triggerName, pipelineId);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Webhook trigger '{TriggerName}' of pipeline '{PipelineId}' failed", triggerName, pipelineId);
        }
    }

    private static WebhookVerification Verification(WebhookTriggerVerify verify, string key) => verify switch
    {
        HmacSha256TriggerVerify h => new WebhookVerification.HmacSha256(key, h.Header, h.Prefix),
        _ => new WebhookVerification.Bearer(key),
    };

    /// <summary>
    /// <c>{headers, body}</c> for the mapper: header names lower-cased, without credentials (Authorization,
    /// cookies, the signature header); the body as JSON when it parses, else as text.
    /// </summary>
    internal static string EventJson(WebhookDelivery delivery, WebhookTriggerVerify verify)
    {
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "authorization", "cookie" };
        if (verify is HmacSha256TriggerVerify h)
            hidden.Add(h.Header);

        var headers = new JsonObject();
        foreach (var (name, value) in delivery.Headers.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!hidden.Contains(name))
                headers[name.ToLowerInvariant()] = value;
        }

        JsonNode? body;
        try
        {
            body = JsonNode.Parse(delivery.Body);
        }
        catch (JsonException)
        {
            body = JsonValue.Create(Encoding.UTF8.GetString(delivery.Body));
        }

        return new JsonObject { ["headers"] = headers, ["body"] = body }.ToJsonString();
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => _lifetime.CancelAsync();

    public void Dispose()
    {
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
