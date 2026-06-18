namespace Olve.Pipelines.Configuration;

/// <summary>
/// Inbound-webhook settings. <see cref="PublicBaseUrl"/> is the externally-reachable origin GitHub
/// must POST deliveries to (e.g. <c>https://pipelines-hooks.ovea.pro</c>); the receiver path
/// <c>/api/webhooks/github/{triggerId}</c> is appended when a hook is registered. Null/empty means
/// auto-registration is disabled (the receiver still works for manually-created hooks).
/// </summary>
public record WebhookOptions(string? PublicBaseUrl);
