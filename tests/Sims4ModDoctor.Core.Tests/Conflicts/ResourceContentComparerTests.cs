using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModDoctor.Core.Conflicts;
using static Sims4ModDoctor.Core.Tests.Conflicts.CandidateFactory;

namespace Sims4ModDoctor.Core.Tests.Conflicts;

[TestClass]
public sealed class ResourceContentComparerTests
{
    private const string HashA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string HashB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private const string HashC = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";

    [TestMethod]
    public async Task SameTgiSameContentIsIdentical()
    {
        var hasher = new FakeResourceContentHasher()
            .WithContent("a.package", 0, HashA)
            .WithContent("b.package", 3, HashA);

        var comparison = (await Compare(hasher, Candidate(Key(), ("a.package", 0), ("b.package", 3)))).Comparisons.Single();

        Assert.AreEqual(ResourceContentVerdict.Identical, comparison.Verdict);
        Assert.IsTrue(comparison.IsComplete);
        Assert.IsFalse(comparison.HasConfirmedDifference);
        Assert.AreEqual(1, comparison.ContentGroups.Count);
        Assert.AreEqual(2, comparison.ContentGroups[0].Occurrences.Count);
    }

    [TestMethod]
    public async Task SameTgiDifferentContentIsDifferent()
    {
        var hasher = new FakeResourceContentHasher()
            .WithContent("a.package", 0, HashA)
            .WithContent("b.package", 0, HashB);

        var comparison = (await Compare(hasher, Candidate(Key(), ("a.package", 0), ("b.package", 0)))).Comparisons.Single();

        Assert.AreEqual(ResourceContentVerdict.Different, comparison.Verdict);
        Assert.IsTrue(comparison.IsComplete);
        Assert.IsTrue(comparison.HasConfirmedDifference);
        Assert.AreEqual(2, comparison.ContentGroups.Count);
    }

    [TestMethod]
    public async Task ManyFilesAreSplitIntoContentSubgroups()
    {
        var hasher = new FakeResourceContentHasher()
            .WithContent("a.package", 0, HashA)
            .WithContent("b.package", 0, HashB)
            .WithContent("c.package", 0, HashA)
            .WithContent("d.package", 0, HashB)
            .WithContent("e.package", 0, HashC);

        var comparison = (await Compare(hasher, Candidate(Key(),
            ("e.package", 0), ("d.package", 0), ("c.package", 0), ("b.package", 0), ("a.package", 0))))
            .Comparisons.Single();

        // 子组按第一次出现的位置排序，组内按路径排序。
        CollectionAssert.AreEqual(
            new[] { HashA, HashB, HashC },
            comparison.ContentGroups.Select(group => group.Sha256).ToArray());
        CollectionAssert.AreEqual(
            new[] { "a.package", "c.package" },
            comparison.ContentGroups[0].Occurrences.Select(occurrence => occurrence.PackagePath).ToArray());
        CollectionAssert.AreEqual(
            new[] { "b.package", "d.package" },
            comparison.ContentGroups[1].Occurrences.Select(occurrence => occurrence.PackagePath).ToArray());
        Assert.AreEqual(ResourceContentVerdict.Different, comparison.Verdict);
    }

    [TestMethod]
    public async Task PartialFailureIsNeverReportedAsIdentical()
    {
        var hasher = new FakeResourceContentHasher()
            .WithContent("a.package", 0, HashA)
            .WithContent("b.package", 0, HashA)
            .WithFailure("c.package", 0);

        var comparison = (await Compare(hasher, Candidate(Key(),
            ("a.package", 0), ("b.package", 0), ("c.package", 0)))).Comparisons.Single();

        Assert.AreEqual(ResourceContentVerdict.Incomplete, comparison.Verdict);
        Assert.IsFalse(comparison.IsComplete);
        Assert.IsFalse(comparison.HasConfirmedDifference);
        Assert.AreEqual("c.package", comparison.Uncompared.Single().PackagePath);
        Assert.AreEqual(ResourceContentIssueCode.Corrupt, comparison.Uncompared.Single().Issue!.Code);
    }

    [TestMethod]
    public async Task PartialFailureKeepsAConfirmedDifference()
    {
        var hasher = new FakeResourceContentHasher()
            .WithContent("a.package", 0, HashA)
            .WithContent("b.package", 0, HashB)
            .WithFailure("c.package", 0, ResourceContentIssueCode.UnsupportedCompression);

        var comparison = (await Compare(hasher, Candidate(Key(),
            ("a.package", 0), ("b.package", 0), ("c.package", 0)))).Comparisons.Single();

        Assert.AreEqual(ResourceContentVerdict.Different, comparison.Verdict);
        Assert.IsTrue(comparison.HasConfirmedDifference);
        Assert.IsFalse(comparison.IsComplete);
        Assert.AreEqual(1, comparison.Uncompared.Count);
    }

    [TestMethod]
    public async Task OnlyOneSuccessfulResourceIsNotIdentical()
    {
        var hasher = new FakeResourceContentHasher()
            .WithContent("a.package", 0, HashA)
            .WithFailure("b.package", 0);

        var comparison = (await Compare(hasher, Candidate(Key(), ("a.package", 0), ("b.package", 0)))).Comparisons.Single();

        Assert.AreEqual(ResourceContentVerdict.Incomplete, comparison.Verdict);
        Assert.AreEqual(1, comparison.ContentGroups.Count);
        Assert.IsFalse(comparison.HasConfirmedDifference);
    }

    [TestMethod]
    public async Task OneFailingPackageDoesNotStopTheOthers()
    {
        var hasher = new FakeResourceContentHasher()
            .WithContent("a.package", 0, HashA)
            .WithContent("b.package", 0, HashB)
            .WithException("c.package", new InvalidOperationException("读取器缺陷"));

        var comparison = (await Compare(hasher, Candidate(Key(),
            ("a.package", 0), ("b.package", 0), ("c.package", 0)))).Comparisons.Single();

        Assert.AreEqual(2, comparison.ContentGroups.Count);
        var failed = comparison.Uncompared.Single();
        Assert.AreEqual(ResourceContentIssueCode.UnexpectedError, failed.Issue!.Code);
        Assert.AreEqual(ResourceContentStage.Unexpected, failed.Issue.Stage);
    }

    [TestMethod]
    public async Task ResultsThatDoNotMatchTheRequestAreDiscarded()
    {
        var hasher = new FakeResourceContentHasher { DropLastResult = true }
            .WithContent("a.package", 0, HashA)
            .WithContent("a.package", 1, HashA)
            .WithContent("b.package", 0, HashA)
            .WithContent("b.package", 1, HashA);

        var report = await Compare(hasher,
            Candidate(Key(1), ("a.package", 0), ("b.package", 0)),
            Candidate(Key(2), ("a.package", 1), ("b.package", 1)));

        // 少了一条结果，不能把剩下的按位置硬对上：整批作废。
        Assert.IsTrue(report.Comparisons.All(comparison => comparison.ContentGroups.Count == 0));
        Assert.IsTrue(report.Comparisons
            .SelectMany(comparison => comparison.Uncompared)
            .All(occurrence => occurrence.Issue!.Code == ResourceContentIssueCode.UnexpectedError));
    }

    [TestMethod]
    public async Task OutOfMemoryIsNotTurnedIntoAResourceFailure()
    {
        var hasher = new FakeResourceContentHasher()
            .WithContent("a.package", 0, HashA)
            .WithException("b.package", new OutOfMemoryException());

        await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            Compare(hasher, Candidate(Key(), ("a.package", 0), ("b.package", 0))));
    }

    [TestMethod]
    public async Task CancellationDuringReadingPropagatesAndNeverReportsCompleted()
    {
        using var cancellation = new CancellationTokenSource();
        var hasher = new FakeResourceContentHasher
        {
            OnHash = path =>
            {
                if (path == "b.package")
                {
                    cancellation.Cancel();
                }
            },
        }
            .WithContent("a.package", 0, HashA)
            .WithContent("b.package", 0, HashA);

        var phases = new ConcurrentQueue<ResourceContentComparisonPhase>();
        var comparer = new ResourceContentComparer(hasher);

        await Assert.ThrowsAsync<OperationCanceledException>(() => comparer.CompareAsync(
            new ResourceContentComparisonRequest(
                [Candidate(Key(), ("a.package", 0), ("b.package", 0))],
                ResourceContentLimits.Default with { MaxDegreeOfParallelism = 1 }),
            new SynchronousProgress<ResourceContentComparisonProgress>(progress => phases.Enqueue(progress.Phase)),
            cancellation.Token));

        Assert.IsFalse(phases.Contains(ResourceContentComparisonPhase.Completed));
    }

    [TestMethod]
    public async Task EachResourceIsReadOnceAndEachPackageOnce()
    {
        var hasher = new FakeResourceContentHasher()
            .WithContent("a.package", 0, HashA)
            .WithContent("a.package", 1, HashB)
            .WithContent("b.package", 0, HashA)
            .WithContent("b.package", 1, HashB)
            .WithContent("c.package", 5, HashB);

        // 同一个 (a, 0) 在输入里出现两次（两组候选重复给出），也只读一次。
        await Compare(hasher,
            Candidate(Key(1), ("a.package", 0), ("b.package", 0)),
            Candidate(Key(2), ("a.package", 1), ("b.package", 1), ("c.package", 5)),
            Candidate(Key(1), ("a.package", 0), ("b.package", 0)));

        var requests = hasher.Requests.ToArray();
        CollectionAssert.AreEquivalent(
            new[] { "a.package", "b.package", "c.package" },
            requests.Select(request => request.PackagePath).ToArray());
        var a = requests.Single(request => request.PackagePath == "a.package");
        CollectionAssert.AreEqual(new[] { 0, 1 }, a.Targets.Select(target => target.Ordinal).ToArray());
        var passedStamp = a.ExpectedStamp;
        Assert.AreEqual(Stamp, passedStamp);
    }

    [TestMethod]
    public async Task ConcurrencyNeverExceedsTheConfiguredLimit()
    {
        var hasher = new FakeResourceContentHasher();
        var occurrences = new List<(string, int)>();
        for (var index = 0; index < 12; index++)
        {
            var path = $"p{index:D2}.package";
            hasher.WithContent(path, 0, HashA).WithDelay(path, TimeSpan.FromMilliseconds(20));
            occurrences.Add((path, 0));
        }

        await new ResourceContentComparer(hasher).CompareAsync(new ResourceContentComparisonRequest(
            [Candidate(Key(), [.. occurrences])],
            ResourceContentLimits.Default with { MaxDegreeOfParallelism = 3 }));

        Assert.IsTrue(hasher.MaxInFlight <= 3, $"并发达到 {hasher.MaxInFlight}");
        Assert.IsGreaterThan(1, hasher.MaxInFlight);
    }

    [TestMethod]
    public async Task AllConcurrentBatchesShareOneBudget()
    {
        var hasher = new FakeResourceContentHasher { BytesPerResource = 10 };
        var occurrences = new List<(string, int)>();
        for (var index = 0; index < 6; index++)
        {
            var path = $"p{index}.package";
            hasher.WithContent(path, 0, HashA);
            occurrences.Add((path, 0));
        }

        var report = await new ResourceContentComparer(hasher).CompareAsync(new ResourceContentComparisonRequest(
            [Candidate(Key(), [.. occurrences])],
            ResourceContentLimits.Default with { MaxTotalProcessedBytes = 35, MaxDegreeOfParallelism = 3 }));

        // 6 条各要 10 字节、总预算 35：不管怎么并发，只能有 3 条拿到预算。
        Assert.AreEqual(1, hasher.Budgets.Distinct().Count());
        var comparison = report.Comparisons.Single();
        Assert.AreEqual(3, comparison.ContentGroups.Single().Occurrences.Count);
        Assert.AreEqual(3, comparison.Uncompared.Count(occurrence =>
            occurrence.Issue!.Code == ResourceContentIssueCode.BudgetExhausted));
        Assert.AreEqual(30, report.ProcessedBytes);
        Assert.AreEqual(ResourceContentVerdict.Incomplete, comparison.Verdict);
    }

    [TestMethod]
    public async Task ResultsDoNotDependOnCompletionOrder()
    {
        async Task<string> Run(string slow)
        {
            var hasher = new FakeResourceContentHasher()
                .WithContent("a.package", 0, HashA)
                .WithContent("b.package", 0, HashB)
                .WithContent("c.package", 0, HashA)
                .WithContent("c.package", 1, HashC)
                .WithContent("d.package", 0, HashC)
                .WithDelay(slow, TimeSpan.FromMilliseconds(40));

            var report = await new ResourceContentComparer(hasher).CompareAsync(new ResourceContentComparisonRequest(
                [
                    Candidate(Key(2), ("d.package", 0), ("c.package", 1)),
                    Candidate(Key(1), ("c.package", 0), ("b.package", 0), ("a.package", 0)),
                ],
                ResourceContentLimits.Default with { MaxDegreeOfParallelism = 4 }));

            return string.Join("|", report.Comparisons.Select(comparison =>
                comparison.Key.Tgi + ":" + string.Join(",", comparison.ContentGroups.Select(group =>
                    group.Sha256[..1] + "=" + string.Join("+", group.Occurrences.Select(o => o.PackagePath))))))
                + "#" + string.Join(",", report.Packages.Select(package => package.Path));
        }

        var first = await Run("a.package");
        Assert.AreEqual(first, await Run("d.package"));
        Assert.AreEqual(first, await Run("c.package"));
        StringAssert.StartsWith(first, "034AEECB:00000000:0000000000000001:");
    }

    [TestMethod]
    public async Task ReturnedCollectionsAreReadOnlyAllTheWayDown()
    {
        var hasher = new FakeResourceContentHasher()
            .WithContent("a.package", 0, HashA)
            .WithContent("b.package", 0, HashA)
            .WithFailure("c.package", 0);

        var report = await Compare(hasher, Candidate(Key(), ("a.package", 0), ("b.package", 0), ("c.package", 0)));
        var comparison = report.Comparisons.Single();

        AssertReadOnly(report.Comparisons);
        AssertReadOnly(report.Packages);
        AssertReadOnly(comparison.ContentGroups);
        AssertReadOnly(comparison.Uncompared);
        AssertReadOnly(comparison.ContentGroups[0].Occurrences);
    }

    [TestMethod]
    public async Task ProgressNeverGoesBackwards()
    {
        var hasher = new FakeResourceContentHasher { BytesPerResource = 7 };
        var occurrences = new List<(string, int)>();
        for (var index = 0; index < 10; index++)
        {
            var path = $"p{index}.package";
            hasher.WithContent(path, 0, HashA).WithDelay(path, TimeSpan.FromMilliseconds(5 * (index % 3)));
            occurrences.Add((path, 0));
        }

        var updates = new ConcurrentQueue<ResourceContentComparisonProgress>();
        await new ResourceContentComparer(hasher).CompareAsync(
            new ResourceContentComparisonRequest(
                [Candidate(Key(), [.. occurrences])],
                ResourceContentLimits.Default with { MaxDegreeOfParallelism = 4 }),
            new SynchronousProgress<ResourceContentComparisonProgress>(updates.Enqueue));

        var list = updates.ToArray();
        for (var index = 1; index < list.Length; index++)
        {
            Assert.IsTrue(list[index].CompletedResources >= list[index - 1].CompletedResources);
            Assert.IsTrue(list[index].ProcessedBytes >= list[index - 1].ProcessedBytes);
        }

        Assert.AreEqual(ResourceContentComparisonPhase.Completed, list[^1].Phase);
        Assert.AreEqual(10, list[^1].CompletedResources);
        Assert.AreEqual(70, list[^1].ProcessedBytes);
    }

    [TestMethod]
    public async Task InvalidLimitsAreRejectedBeforeAnyRead()
    {
        var hasher = new FakeResourceContentHasher().WithContent("a.package", 0, HashA);
        var comparer = new ResourceContentComparer(hasher);
        var candidates = new[] { Candidate(Key(), ("a.package", 0), ("b.package", 0)) };

        foreach (var limits in new[]
        {
            ResourceContentLimits.Default with { MaxStoredBytesPerResource = 0 },
            ResourceContentLimits.Default with { MaxDecompressedBytesPerResource = -1 },
            ResourceContentLimits.Default with { MaxTotalProcessedBytes = 0 },
            ResourceContentLimits.Default with { MaxDegreeOfParallelism = 0 },
        })
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                comparer.CompareAsync(new ResourceContentComparisonRequest(candidates, limits)));
        }

        Assert.AreEqual(0, hasher.Requests.Count);
    }

    [TestMethod]
    public void BudgetRefusesWithoutPartialConsumption()
    {
        var budget = new ResourceContentBudget(10);
        Assert.IsTrue(budget.TryConsume(6));
        Assert.IsFalse(budget.TryConsume(5));
        Assert.AreEqual(6, budget.ConsumedBytes);
        Assert.IsTrue(budget.TryConsume(4));
        Assert.IsFalse(budget.TryConsume(1));
        Assert.AreEqual(10, budget.ConsumedBytes);
    }

    [TestMethod]
    public void BudgetIsSafeUnderContention()
    {
        var budget = new ResourceContentBudget(1000);
        var granted = 0;
        Parallel.For(0, 5000, _ =>
        {
            if (budget.TryConsume(1))
            {
                Interlocked.Increment(ref granted);
            }
        });

        Assert.AreEqual(1000, granted);
        Assert.AreEqual(1000, budget.ConsumedBytes);
    }

    private static Task<ResourceContentComparisonReport> Compare(
        FakeResourceContentHasher hasher,
        params PackageConflictCandidate[] candidates) =>
        new ResourceContentComparer(hasher).CompareAsync(new ResourceContentComparisonRequest(candidates));

    private static void AssertReadOnly<T>(IReadOnlyList<T> items)
    {
        if (items is IList<T> list)
        {
            Assert.IsTrue(list.IsReadOnly, $"{items.GetType().Name} 可以被改写");
            Assert.Throws<NotSupportedException>(() => list.Add(default!));
        }

        Assert.IsFalse(items is T[], "不能直接暴露数组");
        Assert.IsFalse(items is List<T>, "不能直接暴露 List");
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
