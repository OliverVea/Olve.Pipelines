using System.Xml.Linq;
using Amazon.Runtime;

namespace Olve.Pipelines.Configuration;

/// <summary>
/// AWS credentials obtained by exchanging an OIDC token for temporary S3 credentials
/// via STS AssumeRoleWithWebIdentity. Credentials are lazily refreshed when expired.
/// </summary>
public class StsCredentials(
    OAuth2TokenProvider tokenProvider,
    string stsEndpoint,
    string roleArn,
    bool skipCertValidation = false,
    ILogger<StsCredentials>? logger = null) : AWSCredentials
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ImmutableCredentials? _cached;
    private DateTimeOffset _expiry = DateTimeOffset.MinValue;

    public override async Task<ImmutableCredentials> GetCredentialsAsync()
    {
        if (_cached is not null && DateTimeOffset.UtcNow < _expiry.AddSeconds(-60))
        {
            return _cached;
        }

        await _lock.WaitAsync();
        try
        {
            if (_cached is not null && DateTimeOffset.UtcNow < _expiry.AddSeconds(-60))
            {
                return _cached;
            }

            var token = await tokenProvider.GetAccessTokenAsync();

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
            }));

            var xml = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                logger?.LogError("STS token exchange failed: {StatusCode} {Body}", response.StatusCode, xml);
                throw new InvalidOperationException($"STS token exchange failed: {response.StatusCode}");
            }

            var doc = XDocument.Parse(xml);
            var ns = doc.Root!.Name.Namespace;
            var credentials = doc.Descendants(ns + "Credentials").First();

            var accessKeyId = credentials.Element(ns + "AccessKeyId")!.Value;
            var secretAccessKey = credentials.Element(ns + "SecretAccessKey")!.Value;
            var sessionToken = credentials.Element(ns + "SessionToken")!.Value;
            var expiration = DateTimeOffset.Parse(credentials.Element(ns + "Expiration")!.Value);

            _cached = new ImmutableCredentials(accessKeyId, secretAccessKey, sessionToken);
            _expiry = expiration;

            logger?.LogInformation("STS credentials obtained (expires {Expiry})", _expiry);

            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }

    public override ImmutableCredentials GetCredentials() => GetCredentialsAsync().GetAwaiter().GetResult();
}
