using Olve.Pipelines.Configuration;

namespace Olve.Pipelines.Kubernetes;

public class OpenBaoClient : IDisposable
{
    private readonly OAuth2TokenProvider _tokenProvider;
    private readonly string _openBaoUrl;
    private readonly string _role;
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenBaoClient>? _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string? _vaultToken;
    private DateTimeOffset _vaultTokenExpiry = DateTimeOffset.MinValue;

    public OpenBaoClient(
        OAuth2TokenProvider tokenProvider,
        string openBaoUrl,
        string role = "olve-pipelines",
        bool skipCertValidation = false,
        ILogger<OpenBaoClient>? logger = null)
    {
        _tokenProvider = tokenProvider;
        _openBaoUrl = openBaoUrl.TrimEnd('/');
        _role = role;
        _logger = logger;

        _httpClient = skipCertValidation
            ? new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            })
            : new HttpClient();
    }

    public async Task<Dictionary<string, string>> ReadSecretAsync(string path, CancellationToken ct = default)
    {
        var vaultToken = await GetVaultTokenAsync(ct);

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{_openBaoUrl}/v1/{path}");
        request.Headers.Add("X-Vault-Token", vaultToken);

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var secretResponse = await response.Content.ReadFromJsonAsync(
            OpenBaoJsonContext.Default.OpenBaoSecretResponse, ct);

        return secretResponse?.Data?.Data
            ?? throw new InvalidOperationException($"OpenBao secret at '{path}' did not contain data.");
    }

    private async Task<string> GetVaultTokenAsync(CancellationToken ct)
    {
        if (_vaultToken is not null && DateTimeOffset.UtcNow < _vaultTokenExpiry.AddSeconds(-60))
        {
            return _vaultToken;
        }

        await _lock.WaitAsync(ct);
        try
        {
            if (_vaultToken is not null && DateTimeOffset.UtcNow < _vaultTokenExpiry.AddSeconds(-60))
            {
                return _vaultToken;
            }

            var jwt = await _tokenProvider.GetAccessTokenAsync(ct);

            var loginRequest = new OpenBaoLoginRequest(jwt, _role);
            var response = await _httpClient.PostAsJsonAsync(
                $"{_openBaoUrl}/v1/auth/jwt/login",
                loginRequest,
                OpenBaoJsonContext.Default.OpenBaoLoginRequest,
                ct);

            response.EnsureSuccessStatusCode();

            var loginResponse = await response.Content.ReadFromJsonAsync(
                OpenBaoJsonContext.Default.OpenBaoLoginResponse, ct);

            if (loginResponse?.Auth?.ClientToken is null)
            {
                throw new InvalidOperationException("OpenBao login response did not contain a client token.");
            }

            _vaultToken = loginResponse.Auth.ClientToken;
            _vaultTokenExpiry = DateTimeOffset.UtcNow.AddSeconds(loginResponse.Auth.LeaseDuration);

            _logger?.LogInformation("OpenBao vault token obtained (lease: {LeaseDuration}s)", loginResponse.Auth.LeaseDuration);

            return _vaultToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _lock.Dispose();
    }
}
