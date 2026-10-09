using Sims4ModDoctor.Core.Duplicates;
using Sims4ModDoctor.Core.Packages;

namespace Sims4ModDoctor.Core.Conflicts;

public interface IResourceContentComparer
{
    Task<ResourceContentComparisonReport> CompareAsync(
        ResourceContentComparisonRequest request,
        IProgress<ResourceContentComparisonProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 对候选冲突组里相同 TGI 的资源读取内容、计算解压后的 SHA-256，再按内容哈希分组。
/// </summary>
/// <remarks>
/// 资源读取器由调用方注入；Core 只负责拆分任务、并发控制、预算与结果分类。
/// <list type="bullet">
/// <item>同一个 package 的资源合成一批，由读取器一次打开、按同一个文件版本读完。</item>
/// <item>同一条资源（同一路径、同一 ordinal）在一次比较里最多读取一次。</item>
/// <item>结果顺序只取决于输入内容，不取决于任务完成顺序。</item>
/// </list>
/// </remarks>
public sealed class ResourceContentComparer(IResourceContentHasher hasher) : IResourceContentComparer
{
    public Task<ResourceContentComparisonReport> CompareAsync(
        ResourceContentComparisonRequest request,
        CancellationToken cancellationToken) =>
        CompareAsync(request, progress: null, cancellationToken);

    public async Task<ResourceContentComparisonReport> CompareAsync(
        ResourceContentComparisonRequest request,
        IProgress<ResourceContentComparisonProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Candidates);

        var limits = request.Limits ?? ResourceContentLimits.Default;
        limits.Validate();

        cancellationToken.ThrowIfCancellationRequested();
        var startedAt = DateTime.UtcNow;
        var budget = new ResourceContentBudget(limits.MaxTotalProcessedBytes);
        var reporter = new ProgressReporter(progress, budget);

        var batches = BuildBatches(request.Candidates, cancellationToken);
        var total = batches.Sum(batch => batch.Targets.Count);
        reporter.Report(ResourceContentComparisonPhase.Starting, "正在准备比较资源内容…", 0, total);

        var outcomes = await HashBatchesAsync(batches, limits, budget, reporter, total, cancellationToken)
            .ConfigureAwait(false);

        reporter.Report(ResourceContentComparisonPhase.Grouping, "正在按内容分组…", total, total);

        var lookup = new Dictionary<(string Path, FileStamp Stamp, int Ordinal, ResourceKey Key), ResourceContentResult>(
            new OccurrenceKeyComparer());
        for (var index = 0; index < batches.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = batches[index];
            var outcome = outcomes[index];
            for (var target = 0; target < batch.Targets.Count; target++)
            {
                lookup[(batch.Path, batch.Stamp, batch.Targets[target].Ordinal, batch.Targets[target].Key)] =
                    outcome.Results[target];
            }
        }

        var comparisons = ReadOnlyLists.Freeze(request.Candidates
            .Select(candidate =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Classify(candidate, lookup);
            })
            .OrderBy(comparison => comparison.Key.Type)
            .ThenBy(comparison => comparison.Key.Group)
            .ThenBy(comparison => comparison.Key.Instance));

        var packages = ReadOnlyLists.Freeze(batches
            .Select((batch, index) => new ResourceContentPackageVersion(
                batch.Path,
                batch.Stamp,
                outcomes[index].StampAfterRead,
                outcomes[index].StampAfterRead == batch.Stamp
                    && outcomes[index].Results.All(result =>
                        result.Issue?.Stage is not ResourceContentStage.Stability))));

        var report = new ResourceContentComparisonReport(
            startedAt,
            DateTime.UtcNow,
            limits,
            comparisons,
            packages,
            budget.ConsumedBytes);

        // 取消之后不得报告完成，也不得返回一份看起来完整的报告。
        cancellationToken.ThrowIfCancellationRequested();
        reporter.Report(
            ResourceContentComparisonPhase.Completed,
            $"比较完成：{comparisons.Count(comparison => comparison.HasConfirmedDifference)} 组内容不同",
            total,
            total);

        return report;
    }

    private async Task<BatchOutcome[]> HashBatchesAsync(
        IReadOnlyList<Batch> batches,
        ResourceContentLimits limits,
        ResourceContentBudget budget,
        ProgressReporter reporter,
        int total,
        CancellationToken cancellationToken)
    {
        var outcomes = new BatchOutcome[batches.Count];
        var completed = 0;

        reporter.Report(
            ResourceContentComparisonPhase.ReadingContents,
            total == 0 ? "没有需要比较的资源" : "正在读取资源内容…",
            0,
            total);

        // Parallel.ForEachAsync 按并发上限逐个取任务，不会为全部 package 一次性创建 Task。
        await Parallel.ForEachAsync(
                Enumerable.Range(0, batches.Count),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = limits.MaxDegreeOfParallelism,
                    CancellationToken = cancellationToken,
                },
                async (index, token) =>
                {
                    outcomes[index] = await HashOneAsync(batches[index], limits, budget, token).ConfigureAwait(false);

                    var done = Interlocked.Add(ref completed, batches[index].Targets.Count);
                    reporter.Report(
                        ResourceContentComparisonPhase.ReadingContents,
                        $"正在读取资源内容（{done}/{total}）",
                        done,
                        total);
                })
            .ConfigureAwait(false);

        return outcomes;
    }

    private async Task<BatchOutcome> HashOneAsync(
        Batch batch,
        ResourceContentLimits limits,
        ResourceContentBudget budget,
        CancellationToken cancellationToken)
    {
        ResourceContentBatchResult result;
        try
        {
            result = await hasher.HashAsync(
                    new ResourceContentBatchRequest(batch.Path, batch.Stamp, batch.Targets, limits),
                    budget,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 读取器的契约是「可预期的失败返回结果、不抛异常」。越过契约的异常只算这个 package 失败，
            // 其他 package 继续比较。内存不足必须向上传播，不能报告为可继续的失败。
            return FailAll(batch, new ResourceContentIssue(
                ResourceContentIssueCode.UnexpectedError,
                ResourceContentStage.Unexpected,
                "读取这个 package 的资源内容时出现意外错误，已跳过。",
                $"{exception.GetType().FullName}: {exception.Message}"));
        }

        // 结果必须与请求一一对应；对不上就不能信任其中任何一条，否则可能把 A 的哈希记到 B 头上。
        if (result is null
            || result.Results is null
            || result.Results.Count != batch.Targets.Count
            || result.Results.Where((item, index) => item is null || item.Target != batch.Targets[index]).Any())
        {
            return FailAll(batch, new ResourceContentIssue(
                ResourceContentIssueCode.UnexpectedError,
                ResourceContentStage.Unexpected,
                "读取器返回的结果与请求对不上，这个 package 的比较结果已作废。"));
        }

        return new BatchOutcome(result.StampAfterRead, result.Results);
    }

    private static BatchOutcome FailAll(Batch batch, ResourceContentIssue issue) => new(
        null,
        batch.Targets.Select(target => ResourceContentResult.Failure(target, issue)).ToArray());

    /// <summary>
    /// 把候选组拆成按 package 的读取批次。同一路径、同一文件版本的资源合成一批，
    /// 同一条资源只进批次一次。批次与批次内顺序都是确定的。
    /// </summary>
    private static IReadOnlyList<Batch> BuildBatches(
        IReadOnlyList<PackageConflictCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var byPackage = new Dictionary<(string Path, FileStamp Stamp), SortedSet<ResourceContentTarget>>(
            new PackageVersionComparer());

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(candidate);
            foreach (var occurrence in candidate.Occurrences)
            {
                var id = (occurrence.PackagePath, occurrence.PackageStamp);
                if (!byPackage.TryGetValue(id, out var targets))
                {
                    targets = new SortedSet<ResourceContentTarget>(TargetComparer.Instance);
                    byPackage.Add(id, targets);
                }

                targets.Add(new ResourceContentTarget(occurrence.Key, occurrence.Ordinal));
            }
        }

        return byPackage
            .OrderBy(pair => pair.Key.Path, PathRules.Comparer)
            .ThenBy(pair => pair.Key.Path, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Stamp.Length)
            .ThenBy(pair => pair.Key.Stamp.LastWriteTimeUtc)
            .Select(pair => new Batch(pair.Key.Path, pair.Key.Stamp, pair.Value.ToArray()))
            .ToArray();
    }

    private static ResourceContentComparison Classify(
        PackageConflictCandidate candidate,
        IReadOnlyDictionary<(string Path, FileStamp Stamp, int Ordinal, ResourceKey Key), ResourceContentResult> lookup)
    {
        var occurrences = candidate.Occurrences
            .Select(occurrence =>
            {
                var result = lookup[(occurrence.PackagePath, occurrence.PackageStamp, occurrence.Ordinal, occurrence.Key)];
                return new ResourceContentOccurrence(
                    occurrence.Key,
                    occurrence.PackagePath,
                    occurrence.Ordinal,
                    occurrence.PackageStamp,
                    result.Sha256,
                    result.DecompressedLength,
                    result.Issue);
            })
            .OrderBy(occurrence => occurrence.PackagePath, PathRules.Comparer)
            .ThenBy(occurrence => occurrence.PackagePath, StringComparer.Ordinal)
            .ThenBy(occurrence => occurrence.Ordinal)
            .ToArray();

        // 子组按「第一次出现的位置」排序；内容相同则哈希相同、长度必然相同。
        var groups = occurrences
            .Where(occurrence => occurrence.IsSuccess)
            .GroupBy(occurrence => occurrence.Sha256!, StringComparer.Ordinal)
            .Select(group => new ResourceContentSubgroup(
                group.Key,
                group.First().DecompressedLength!.Value,
                ReadOnlyLists.Freeze(group)))
            .ToArray();

        return new ResourceContentComparison(
            candidate.Key,
            ReadOnlyLists.Freeze(groups),
            ReadOnlyLists.Freeze(occurrences.Where(occurrence => !occurrence.IsSuccess)));
    }

    private sealed record Batch(string Path, FileStamp Stamp, IReadOnlyList<ResourceContentTarget> Targets);

    private sealed record BatchOutcome(FileStamp? StampAfterRead, IReadOnlyList<ResourceContentResult> Results);

    private sealed class TargetComparer : IComparer<ResourceContentTarget>
    {
        public static TargetComparer Instance { get; } = new();

        public int Compare(ResourceContentTarget x, ResourceContentTarget y)
        {
            var byOrdinal = x.Ordinal.CompareTo(y.Ordinal);
            if (byOrdinal != 0)
            {
                return byOrdinal;
            }

            var byType = x.Key.Type.CompareTo(y.Key.Type);
            if (byType != 0)
            {
                return byType;
            }

            var byGroup = x.Key.Group.CompareTo(y.Key.Group);
            return byGroup != 0 ? byGroup : x.Key.Instance.CompareTo(y.Key.Instance);
        }
    }

    private sealed class PackageVersionComparer : IEqualityComparer<(string Path, FileStamp Stamp)>
    {
        public bool Equals((string Path, FileStamp Stamp) x, (string Path, FileStamp Stamp) y) =>
            PathRules.Comparer.Equals(x.Path, y.Path) && x.Stamp == y.Stamp;

        public int GetHashCode((string Path, FileStamp Stamp) obj) =>
            HashCode.Combine(PathRules.Comparer.GetHashCode(obj.Path), obj.Stamp);
    }

    private sealed class OccurrenceKeyComparer
        : IEqualityComparer<(string Path, FileStamp Stamp, int Ordinal, ResourceKey Key)>
    {
        public bool Equals(
            (string Path, FileStamp Stamp, int Ordinal, ResourceKey Key) x,
            (string Path, FileStamp Stamp, int Ordinal, ResourceKey Key) y) =>
            PathRules.Comparer.Equals(x.Path, y.Path)
            && x.Stamp == y.Stamp
            && x.Ordinal == y.Ordinal
            && x.Key == y.Key;

        public int GetHashCode((string Path, FileStamp Stamp, int Ordinal, ResourceKey Key) obj) =>
            HashCode.Combine(PathRules.Comparer.GetHashCode(obj.Path), obj.Stamp, obj.Ordinal, obj.Key);
    }

    /// <summary>
    /// 进度汇报。并发任务会同时汇报，这里加锁并保证已完成数与处理字节数只增不减。
    /// </summary>
    private sealed class ProgressReporter(
        IProgress<ResourceContentComparisonProgress>? progress,
        ResourceContentBudget budget)
    {
        private readonly object _gate = new();
        private int _completed;
        private long _bytes;

        public void Report(ResourceContentComparisonPhase phase, string message, int completed, int total)
        {
            if (progress is null)
            {
                return;
            }

            lock (_gate)
            {
                _completed = Math.Max(_completed, completed);
                _bytes = Math.Max(_bytes, budget.ConsumedBytes);
                progress.Report(new ResourceContentComparisonProgress(phase, message, _completed, total, _bytes));
            }
        }
    }
}
