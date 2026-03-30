namespace Olve.Pipelines.Configuration;

public record OTelCredentials(string BearerToken);

public class OTelCredentialsProvider(
    OAuth2TokenProvider tokenProvider) : ICredentialsProvider<OTelCredentials>
{
    public async Task<OTelCredentials> GetCredentialsAsync(CancellationToken ct = default)
    {
        var token = await tokenProvider.GetAccessTokenAsync(ct);
        return new OTelCredentials(token);
    }
}
