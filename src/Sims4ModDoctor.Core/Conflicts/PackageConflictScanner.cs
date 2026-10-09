using Sims4ModDoctor.Core.Duplicates;
using Sims4ModDoctor.Core.Packages;

namespace Sims4ModDoctor.Core.Conflicts;

public interface IPackageConflictScanner
{
    Task<PackageConflictScanReport> ScanAsync(
        PackageConflictScanRequest request,
        IProgress<PackageConflictScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 候选冲突扫描：读取各 package 的资源索引，建立 TGI → package 的倒排，
/// 列出出现在至少两个不同 package 里的 TGI。
/// </summary>
/// <remarks>
/// 只看索引，不读取、不解压、不比较资源内容，因此结果只是「候选」。
/// package 读取器由调用方注入；Core 不引用任何第三方 package 库。
/// </remarks>
public sealed class PackageConflictScanner(
    IFileSystemAccess fileSystem,
    IPackageIndexReader indexReader) : IPackageConflictScanner
{
    public const string PackageExtension = ".package";

    /// <summary>
    /// 索引读取以文件 IO 为主，解析本身不重；并发过高只会让机械硬盘来回寻道。
    /// </summary>
    public static int DefaultMaxDegreeOfParallelism { get; } = Math.Clamp(Environment.ProcessorCount, 1, 4);

    private readonly SourceFileDiscovery _discovery = new(fileSystem);

    public Task<PackageConflictScanReport> ScanAsync(
        PackageConflictScanRequest request,
        CancellationToken cancellationToken) =>
        ScanAsync(request, progress: null, cancellationToken);

    public async Task<PackageConflictScanReport> ScanAsync(
        PackageConflictScanRequest request,
        IProgress<PackageConflictScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Sources);

        var parallelism = request.MaxDegreeOfParallelism ?? DefaultMaxDegreeOfParallelism;
        ArgumentOutOfRangeException.ThrowIfLessThan(parallelism, 1, nameof(request.MaxDegreeOfParallelism));

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new PackageConflictScanProgress(
            PackageConflictScanPhase.Starting,
            "正在准备扫描…"));

        var startedAt = DateTime.UtcNow;
        var sourceIssues = new List<ScanIssue>();
        var sources = SourceFileDiscovery.NormalizeSources(request.Sources, sourceIssues);
        var validSources = _discovery.ValidateSources(sources, sourceIssues);
        var sourceSnapshots = SourceFileDiscovery.CreateSourceSnapshots(sources);

        progress?.Report(new PackageConflictScanProgress(
            PackageConflictScanPhase.Discovering,
            "正在查找 package 文件…"));

        var discovered = _discovery.DiscoverFiles(
            validSources,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PackageExtension },
            sourceIssues,
            progress is null
                ? null
                : (message, count) => progress.Report(new PackageConflictScanProgress(
                    PackageConflictScanPhase.Discovering,
                    message,
                    DiscoveredPackageCount: count)),
            cancellationToken);

        // 读取顺序与并发完成顺序都不影响结果：每个结果写回自己在这个数组里的位置。
        var packagePaths = discovered.Keys
            .OrderBy(path => path, PathRules.Comparer)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var results = await ReadIndexesAsync(packagePaths, parallelism, progress, cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(new PackageConflictScanProgress(
            PackageConflictScanPhase.Grouping,
            "正在比对资源编号…",
            packagePaths.Length,
            packagePaths.Length,
            packagePaths.Length));

        var issues = sourceIssues.Select(FromScanIssue).ToList();
        var incomplete = new List<string>();
        var analyzed = new List<AnalyzedPackage>();
        for (var index = 0; index < packagePaths.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = results[index];
            if (outcome.Summary is not null)
            {
                // 以发现阶段规范化后的路径为准，不依赖读取器怎样拼写路径。
                analyzed.Add(new AnalyzedPackage(packagePaths[index], outcome.Summary));
            }
            else
            {
                incomplete.Add(packagePaths[index]);
                issues.Add(outcome.Issue!);
            }
        }

        var candidates = BuildCandidates(analyzed, validSources, cancellationToken);
        var report = new PackageConflictScanReport(
            startedAt,
            DateTime.UtcNow,
            ReadOnlyLists.Freeze(sourceSnapshots),
            packagePaths.Length,
            analyzed.Count,
            ReadOnlyLists.Freeze(incomplete),
            candidates,
            OrderIssues(issues));

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new PackageConflictScanProgress(
            PackageConflictScanPhase.Completed,
            candidates.Count == 0
                ? "扫描完成，没有发现候选冲突"
                : $"扫描完成，发现 {candidates.Count} 组候选冲突",
            packagePaths.Length,
            packagePaths.Length,
            packagePaths.Length));

        return report;
    }

    private async Task<ReadOutcome[]> ReadIndexesAsync(
        IReadOnlyList<string> packagePaths,
        int parallelism,
        IProgress<PackageConflictScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var results = new ReadOutcome[packagePaths.Count];
        var completed = 0;

        progress?.Report(new PackageConflictScanProgress(
            PackageConflictScanPhase.ReadingIndexes,
            packagePaths.Count == 0 ? "没有需要读取的 package" : "正在读取 package 索引…",
            0,
            packagePaths.Count,
            packagePaths.Count));

        // Parallel.ForEachAsync 按并发上限逐个取任务，不会为全部文件一次性创建 Task。
        await Parallel.ForEachAsync(
                Enumerable.Range(0, packagePaths.Count),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = parallelism,
                    CancellationToken = cancellationToken,
                },
                async (index, token) =>
                {
                    results[index] = await ReadOneAsync(packagePaths[index], token).ConfigureAwait(false);

                    var done = Interlocked.Increment(ref completed);
                    progress?.Report(new PackageConflictScanProgress(
                        PackageConflictScanPhase.ReadingIndexes,
                        $"正在读取 package 索引（{done}/{packagePaths.Count}）",
                        done,
                        packagePaths.Count,
                        packagePaths.Count));
                })
            .ConfigureAwait(false);

        return results;
    }

    private async Task<ReadOutcome> ReadOneAsync(string path, CancellationToken cancellationToken)
    {
        PackageReadResult result;
        try
        {
            result = await indexReader.ReadIndexAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 读取器的契约是「可预期的失败返回结果、不抛异常」。越过契约的异常同样只算这一个文件失败，
            // 不能让一个坏文件或一个读取器缺陷中断整次扫描。
            // 内存不足必须向上传播，与底层读取器一致，不能报告为可继续的单文件失败。
            return new ReadOutcome(null, new PackageConflictScanIssue(
                PackageConflictScanIssueCode.UnexpectedReadError,
                PackageConflictScanIssueStage.IndexRead,
                path,
                "读取这个 package 的索引时出现意外错误，已跳过。",
                Detail: $"{exception.GetType().FullName}: {exception.Message}"));
        }

        if (result.Summary is not null)
        {
            return new ReadOutcome(result.Summary, null);
        }

        var issue = result.Issue!;
        return new ReadOutcome(null, new PackageConflictScanIssue(
            issue.Code,
            PackageConflictScanIssueStage.IndexRead,
            path,
            issue.Message,
            ReadStage: issue.Stage,
            Detail: issue.Detail));
    }

    private static IReadOnlyList<PackageConflictCandidate> BuildCandidates(
        IReadOnlyList<AnalyzedPackage> packages,
        IReadOnlyList<NormalizedSource> sources,
        CancellationToken cancellationToken)
    {
        var byKey = new Dictionary<ResourceKey, List<PackageResourceOccurrence>>();
        foreach (var package in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceIds = SourceIdsFor(package.Path, sources);

            foreach (var entry in package.Summary.Resources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!byKey.TryGetValue(entry.Key, out var occurrences))
                {
                    occurrences = [];
                    byKey.Add(entry.Key, occurrences);
                }

                occurrences.Add(new PackageResourceOccurrence(
                    entry.Key,
                    package.Path,
                    sourceIds,
                    entry.Ordinal,
                    entry.ContentSize,
                    entry.Compression,
                    entry.CompressionRaw));
            }
        }

        // 筛选和结果构建也可能遍历大量资源；在每次枚举时响应取消。
        var orderedCandidates = EnumerateWithCancellation(byKey, cancellationToken)
            .Where(pair => EnumerateWithCancellation(pair.Value, cancellationToken)
                .Select(occurrence => occurrence.PackagePath)
                .Distinct(PathRules.Comparer)
                .Skip(1)
                .Any())
            .OrderBy(pair => pair.Key.Type)
            .ThenBy(pair => pair.Key.Group)
            .ThenBy(pair => pair.Key.Instance);

        return ReadOnlyLists.Freeze(EnumerateWithCancellation(orderedCandidates, cancellationToken)
            .Select(pair => new PackageConflictCandidate(
                pair.Key,
                ReadOnlyLists.Freeze(EnumerateWithCancellation(
                    EnumerateWithCancellation(pair.Value, cancellationToken)
                    .OrderBy(occurrence => occurrence.PackagePath, PathRules.Comparer)
                    .ThenBy(occurrence => occurrence.PackagePath, StringComparer.Ordinal)
                    .ThenBy(occurrence => occurrence.Ordinal), cancellationToken)))));
    }

    private static IEnumerable<T> EnumerateWithCancellation<T>(
        IEnumerable<T> items,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static IReadOnlyList<string> SourceIdsFor(string path, IReadOnlyList<NormalizedSource> sources) =>
        ReadOnlyLists.Freeze(sources
            .Where(source => PathRules.IsWithin(path, source.Path))
            .OrderBy(source => source.Order)
            .ThenBy(source => source.InputIndex)
            .Select(source => source.Id));

    private static PackageConflictScanIssue FromScanIssue(ScanIssue issue) => new(
        issue.Code,
        issue.Stage switch
        {
            ScanIssueStage.SourceValidation => PackageConflictScanIssueStage.SourceValidation,
            ScanIssueStage.Discovery => PackageConflictScanIssueStage.Discovery,
            ScanIssueStage.Metadata => PackageConflictScanIssueStage.Metadata,
            _ => throw new ArgumentOutOfRangeException(nameof(issue), issue.Stage, "发现阶段不会产生这一类问题。"),
        },
        issue.Path,
        issue.Message,
        issue.SourceId);

    private static IReadOnlyList<PackageConflictScanIssue> OrderIssues(IEnumerable<PackageConflictScanIssue> issues) =>
        ReadOnlyLists.Freeze(issues
            .OrderBy(issue => issue.Stage)
            .ThenBy(issue => issue.Path, PathRules.Comparer)
            .ThenBy(issue => issue.Path, StringComparer.Ordinal)
            .ThenBy(issue => issue.Code, StringComparer.Ordinal));

    private sealed record AnalyzedPackage(string Path, PackageIndexSummary Summary);

    private sealed record ReadOutcome(PackageIndexSummary? Summary, PackageConflictScanIssue? Issue);
}
