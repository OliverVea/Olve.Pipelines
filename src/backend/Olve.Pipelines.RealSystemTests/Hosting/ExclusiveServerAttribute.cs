using TUnit.Core.Interfaces;

namespace Olve.Pipelines.RealSystemTests.Hosting;

/// <summary>
/// The test gets a host to itself: it starts once other tests have left the host, and the host is reset
/// afterwards. With <see cref="Fresh"/> it also starts on a freshly reset host. For server-wide or
/// disruptive tests, so rare.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ExclusiveServerAttribute : Attribute, ITestStartEventReceiver
{
    internal const string Key = "olve-exclusive-server";

    public bool Fresh { get; init; }

    public ValueTask OnTestStart(TestContext context)
    {
        context.StateBag.Items[Key] = Fresh;
        return ValueTask.CompletedTask;
    }
}
