using Olve.Pipelines.Api;

namespace Olve.Pipelines.Configuration;

/// <summary>The OIDC authority, client id and scopes the web UI and <c>pl login</c> use.</summary>
public sealed class FrontendConfigHandler(IConfiguration config) : IInstanceFrontendConfigHandler
{
    /// <summary>Space-separated scopes to request; <c>groups</c> only when <c>Auth:Allow:Orgs</c> needs the claim.</summary>
    public static string Scopes(AuthAllowList allow) =>
        allow.Orgs.Count > 0 ? "openid profile email groups" : "openid profile email";

    public Task<InstanceFrontendConfigResponse> HandleAsync(InstanceFrontendConfigRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<InstanceFrontendConfigResponse>(new FrontendConfig
        {
            OidcAuthority = config["Auth:FrontendAuthority"],
            OidcClientId = config["Auth:FrontendAudience"],
            OidcScopes = Scopes(AuthAllowList.From(config)),
        });
}
