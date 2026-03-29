using Duende.IdentityModel.Client;

namespace Olve.Pipelines.Configuration;

public class OAuth2TokenProvider(
    string tokenUrl,
    string clientId,
    string clientSecret,
    string? scope = null,
    bool skipCertValidation = false,
    ILogger<OAuth2TokenProvider>? logger = null) : IDisposable
{
    private readonly HttpClient _httpClient = skipCertValidation
        ? new(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        })
        : new();

    private readonly SemaphoreSlim _lock = new(1, 1);

    private string? _cachedToken;
    private DateTimeOffset _expiry = DateTimeOffset.MinValue;

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _expiry.AddSeconds(-60))
        {
            return _cachedToken;
        }

        await _lock.WaitAsync(ct);
        try
        {
            if (_cachedToken is not null && DateTimeOffset.UtcNow < _expiry.AddSeconds(-60))
            {
                return _cachedToken;
            }

            logger?.LogInformation("Requesting OAuth2 token from {TokenUrl} for client {ClientId}", tokenUrl, clientId);

            var response = await _httpClient.RequestClientCredentialsTokenAsync(new ClientCredentialsTokenRequest
            {
                Address = tokenUrl,
                ClientId = clientId,
                ClientSecret = clientSecret,
                Scope = scope,
            }, ct);

            if (response.IsError)
            {
                logger?.LogError("OAuth2 token request failed: {Error} - {ErrorDescription}",
                    response.Error, response.ErrorDescription);
                throw new InvalidOperationException($"OAuth2 token request failed: {response.Error}");
            }

            _cachedToken = response.AccessToken!;
            _expiry = DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn);

            logger?.LogInformation("OAuth2 token obtained (expires in {ExpiresIn}s)", response.ExpiresIn);

            return _cachedToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    public string GetAccessToken() => GetAccessTokenAsync().GetAwaiter().GetResult();

    public void Dispose()
    {
        _httpClient.Dispose();
        _lock.Dispose();
    }
}
