using Olve.Pipelines.RealSystemTests.Hosting;

namespace Olve.Pipelines.UnitTests.RealSystem;

public class ServerPoolTests
{
    private static ServerPool<string> Pool(string[] hosts, Func<string, Task>? reset = null, int sharedLimit = 8) =>
        new(hosts, reset ?? (_ => Task.CompletedTask), sharedLimit);

    [Test]
    public async Task SharedLeases_SpreadOverTheHosts()
    {
        var pool = Pool(["a", "b"]);

        var first = await pool.AcquireSharedAsync();
        var second = await pool.AcquireSharedAsync();

        await Assert.That(new[] { first.Host, second.Host }).IsEquivalentTo(["a", "b"]);
    }

    [Test]
    public async Task SharedLeases_WaitAtTheLimit_UntilOneIsReleased()
    {
        var pool = Pool(["a"], sharedLimit: 1);
        var first = await pool.AcquireSharedAsync();

        var second = pool.AcquireSharedAsync();
        await Assert.That(second.IsCompleted).IsFalse();

        await first.DisposeAsync();
        await Assert.That((await second).Host).IsEqualTo("a");
    }

    [Test]
    public async Task Exclusive_WaitsForTheHostToDrain_AndHoldsOthersOff()
    {
        var pool = Pool(["a"]);
        var shared = await pool.AcquireSharedAsync();

        var exclusive = pool.AcquireExclusiveAsync();
        var late = pool.AcquireSharedAsync();
        await Assert.That(exclusive.IsCompleted).IsFalse();
        await Assert.That(late.IsCompleted).IsFalse();

        await shared.DisposeAsync();
        var lease = await exclusive;
        await Assert.That(late.IsCompleted).IsFalse();

        await lease.DisposeAsync();
        await Assert.That((await late).Host).IsEqualTo("a");
    }

    [Test]
    public async Task WhileOneHostDrains_OtherTestsUseTheOther()
    {
        var pool = Pool(["a", "b"]);
        var onA = await pool.AcquireSharedAsync();

        var exclusive = await pool.AcquireExclusiveAsync();
        var other = await pool.AcquireSharedAsync();

        await Assert.That(exclusive.Host).IsEqualTo("b");
        await Assert.That(other.Host).IsEqualTo("a");
        await onA.DisposeAsync();
    }

    [Test]
    public async Task Exclusive_ResetsTheHostAfterwards()
    {
        var resets = new List<string>();
        var pool = Pool(["a"], h => { resets.Add(h); return Task.CompletedTask; });

        await (await pool.AcquireExclusiveAsync()).DisposeAsync();

        await Assert.That(resets).IsEquivalentTo(["a"]);
    }

    [Test]
    public async Task Fresh_ResetsFirst_UnlessTheHostIsStillClean()
    {
        var resets = 0;
        var pool = Pool(["a"], _ => { resets++; return Task.CompletedTask; });

        await (await pool.AcquireExclusiveAsync(fresh: true)).DisposeAsync();
        await Assert.That(resets).IsEqualTo(2);

        await (await pool.AcquireExclusiveAsync(fresh: true)).DisposeAsync();
        await Assert.That(resets).IsEqualTo(3);

        await (await pool.AcquireSharedAsync()).DisposeAsync();
        await (await pool.AcquireExclusiveAsync(fresh: true)).DisposeAsync();
        await Assert.That(resets).IsEqualTo(5);
    }

    [Test]
    public async Task HostThatCannotBeReset_LeavesThePool_AndOthersCarryOn()
    {
        var pool = Pool(["a", "b"], h => h == "a" ? throw new InvalidOperationException("boom") : Task.CompletedTask);
        var failing = await pool.AcquireExclusiveAsync();
        var healthy = await pool.AcquireExclusiveAsync();
        await Assert.That(failing.Host).IsEqualTo("a");

        await failing.DisposeAsync();
        await healthy.DisposeAsync();

        await Assert.That((await pool.AcquireSharedAsync()).Host).IsEqualTo("b");
        await Assert.That((await pool.AcquireSharedAsync()).Host).IsEqualTo("b");
    }

    [Test]
    public async Task LastHostThatCannotBeReset_FailsTheRunClearly()
    {
        var pool = Pool(["a"], _ => throw new InvalidOperationException("boom"));
        var lease = await pool.AcquireExclusiveAsync();
        var waiting = pool.AcquireSharedAsync();

        await lease.DisposeAsync();

        var thrown = await Assert.That(async () => await waiting).Throws<InvalidOperationException>();
        await Assert.That(thrown!.Message).IsEqualTo("No test host is left. Resetting host a failed, so it left the pool: boom");
    }
}
