using Olve.Pipelines.Health;

namespace Olve.Pipelines.UnitTests;

public class OutOfMemoryGuardTests
{
    private static void ThrowAndCatch(Exception exception)
    {
        try
        {
            throw exception;
        }
        catch
        {
            // Caught, as Kestrel or a timer loop would: the guard still sees it.
        }
    }

    [Test]
    public async Task AnOutOfMemory_EndsTheProcessOnce_EvenWhenCaught_AndNothingElseDoes()
    {
        var oom = new OutOfMemoryException();
        var failed = new List<Exception>();
        var guard = OutOfMemoryGuard.Install(e =>
        {
            lock (failed)
                failed.Add(e);
        });

        ThrowAndCatch(new InvalidOperationException("not this"));
        ThrowAndCatch(oom);
        ThrowAndCatch(new OutOfMemoryException());
        guard.Dispose();
        ThrowAndCatch(new OutOfMemoryException());

        await Assert.That(failed).IsEquivalentTo(new Exception[] { oom });
    }
}
