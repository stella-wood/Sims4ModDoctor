using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Conflicts;

namespace Sims4ModSieve.Core.Tests.Conflicts;

[TestClass]
public sealed class ResourceContentBudgetTests
{
    [TestMethod]
    public async Task TemporaryReservationDoesNotLookLikeExhaustion()
    {
        var budget = new ResourceContentBudget(10);
        using var first = budget.ReserveUpTo(10);
        var waiting = budget.ReserveUpToAsync(10).AsTask();
        Assert.IsFalse(waiting.IsCompleted);
        first.Complete(2);
        using var second = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(8, second.GrantedBytes);
        second.Complete(8);
        Assert.AreEqual(10L, budget.ConsumedBytes);
    }

    [TestMethod]
    public async Task WaitingForBudgetCanBeCanceledWithoutLeakingAllowance()
    {
        var budget = new ResourceContentBudget(10);
        using var first = budget.ReserveUpTo(10);
        using var source = new CancellationTokenSource();
        var waiting = budget.ReserveUpToAsync(10, cancellationToken: source.Token).AsTask();
        source.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);
        first.Dispose();
        Assert.IsTrue(budget.TryConsume(10));
    }

    [TestMethod]
    public void ShortOperationRefundsUnusedReservation()
    {
        var budget = new ResourceContentBudget(16);
        using (var lease = budget.ReserveUpTo(8, bytesPerUnit: 2))
        {
            Assert.AreEqual(8, lease.GrantedBytes);
            Assert.IsFalse(budget.TryConsume(1));
            Assert.AreEqual(0L, budget.ConsumedBytes);
            lease.Complete(1);
        }
        Assert.AreEqual(2L, budget.ConsumedBytes);
        Assert.IsTrue(budget.TryConsume(14));
    }

    [TestMethod]
    public void FailedOperationReleasesReservation()
    {
        var budget = new ResourceContentBudget(10);
        Assert.Throws<IOException>(() =>
        {
            using var lease = budget.ReserveUpTo(10);
            throw new IOException("simulated IO failure");
        });
        Assert.IsTrue(budget.TryConsume(10));
    }

    [TestMethod]
    public void ConcurrentReservationsAndDirectConsumptionShareOneLimit()
    {
        var budget = new ResourceContentBudget(1000);
        long processed = 0;
        Parallel.For(0, 4000, i =>
        {
            if ((i & 1) == 0)
            {
                if (budget.TryConsume(1)) Interlocked.Increment(ref processed);
            }
            else
            {
                using var lease = budget.ReserveUpTo(3);
                var actual = Math.Min(lease.GrantedBytes, 1);
                lease.Complete(actual);
                Interlocked.Add(ref processed, actual);
            }
        });
        Assert.AreEqual(processed, budget.ConsumedBytes);
        Assert.IsTrue(processed <= budget.TotalBytes);
        Assert.IsTrue(budget.TryConsume(budget.TotalBytes - processed));
    }
}
