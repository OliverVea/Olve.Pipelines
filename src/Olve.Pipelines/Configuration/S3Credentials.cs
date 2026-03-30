namespace Olve.Pipelines.Configuration;

public record S3Credentials(string AccessKey, string SecretKey, string? SessionToken = null);

public class StsCredentialsProvider(
    OAuth2TokenProvider tokenProvider,
    string stsEndpoint,
    string roleArn,
    bool skipCertValidation = false,
    ILogger<StsCredentialsProvider>? logger = null) : ICredentialsProvider<S3Credentials>
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private S3Credentials? _cached;
    private DateTimeOffset _expiry = DateTimeOffset.MinValue;

    public async Task<S3Credentials> GetCredentialsAsync(CancellationToken ct = default)
    {
        if (_cached is not null && DateTimeOffset.UtcNow < _expiry.AddSeconds(-60))
            return _cached;

        await _lock.WaitAsync(ct);
        try
        {
            if (_cached is not null && DateTimeOffset.UtcNow < _expiry.AddSeconds(-60))
                return _cached;

            var token = await tokenProvider.GetAccessTokenAsync(ct);

            using var httpClient = skipCertValidation
                ? new HttpClient(new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                })
                : new HttpClient();

            var response = await httpClient.PostAsync(stsEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Action"] = "AssumeRoleWithWebIdentity",
                ["WebIdentityToken"] = token,
                ["RoleArn"] = roleArn,
                ["Version"] = "2011-06-15",
            }), ct);

            var xml = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                logger?.LogError("STS token exchange failed: {StatusCode} {Body}", response.StatusCode, xml);
                throw new InvalidOperationException($"STS token exchange failed: {response.StatusCode}");
            }

            var doc = System.Xml.Linq.XDocument.Parse(xml);
            var ns = doc.Root!.Name.Namespace;
            var credentials = doc.Descendants(ns + "Credentials").First();

            var accessKeyId = credentials.Element(ns + "AccessKeyId")!.Value;
            var secretAccessKey = credentials.Element(ns + "SecretAccessKey")!.Value;
            var sessionToken = credentials.Element(ns + "SessionToken")!.Value;
            var expiration = DateTimeOffset.Parse(credentials.Element(ns + "Expiration")!.Value);

            _cached = new S3Credentials(accessKeyId, secretAccessKey, sessionToken);
            _expiry = expiration;

            logger?.LogInformation("STS credentials obtained (expires {Expiry})", _expiry);

            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }
}
