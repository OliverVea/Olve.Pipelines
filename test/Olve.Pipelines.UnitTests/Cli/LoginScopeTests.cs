using Olve.Pipelines.Cli.Api.Contracts;
using Olve.Pipelines.Cli.Commands;

namespace Olve.Pipelines.UnitTests.Cli;

public class LoginScopeTests
{
    [Test]
    public async Task Scope_IsTheServersScopes_PlusOfflineAccess() =>
        await Assert.That(LoginCommand.Scope(new FrontendOidcConfig { OidcScopes = "openid profile email groups" }))
            .IsEqualTo("openid profile email groups offline_access");

    [Test]
    public async Task Scope_FromAnOlderServerWithoutScopes_IsTheDefault() =>
        await Assert.That(LoginCommand.Scope(new FrontendOidcConfig())).IsEqualTo("openid profile email offline_access");
}
