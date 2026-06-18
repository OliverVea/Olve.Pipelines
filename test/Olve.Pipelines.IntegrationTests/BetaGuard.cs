using TUnit.Core.Exceptions;

namespace Olve.Pipelines.IntegrationTests;

/// <summary>
/// Shared skip guard for beta-dependent tests. The suite targets the live beta instance over HTTP;
/// without one (a plain local <c>dotnet test</c>), these tests skip rather than fail so the suite
/// stays runnable everywhere. In the <c>.pipelines</c> beta-e2e step, beta is always available, so
/// nothing skips and the suite genuinely gates prod.
/// </summary>
internal static class BetaGuard
{
    public static void SkipIfNoBeta()
    {
        if (!AppFixture.BetaAvailable)
            throw new SkipTestException("Requires a beta instance (set BETA_BASE_URL, or unset BETA_DISABLED)");
    }
}
