using System.Collections.ObjectModel;
using Sims4ModDoctor.Core.Duplicates;
using Sims4ModDoctor.Core.Packages;

namespace Sims4ModDoctor.Core.Conflicts;

/// <summary>
/// 候选冲突扫描请求。
/// </summary>
/// <param name="MaxDegreeOfParallelism">
/// 同时读取索引的 package 数上限；<see langword="null"/> 使用
/// <see cref="PackageConflictScanner.DefaultMaxDegreeOfParallelism"/>。
/// </param>
public sealed record PackageConflictScanRequest(
    IReadOnlyList<ScanSource> Sources,
    int? MaxDegreeOfParallelism = null);

public enum PackageConflictScanPhase
{
    Starting,
    Discovering,
    ReadingIndexes,
    Grouping,
    Completed,
}

public sealed record PackageConflictScanProgress(
    PackageConflictScanPhase Phase,
    string Message,
    int CompletedItems = 0,
    int? TotalItems = null,
    int DiscoveredPackageCount = 0);

/// <summary>
/// 某个 TGI 在某个 package 索引里的一次出现。
/// 元数据原样取自索引，不读取也不解压资源内容。
/// </summary>
/// <param name="PackageStamp">
/// 读取这份索引时 package 的文件戳。下一阶段读取资源内容时以它为期望版本：
/// 文件在候选扫描之后被改过，索引里的位置就不再可信，相关比较结果必须作废。
/// </param>
/// <param name="SourceIds">
/// 包含该 package 的所有启用来源，按来源顺序排列。父子目录同时配置时会有多个。
/// </param>
public sealed record PackageResourceOccurrence(
    ResourceKey Key,
    string PackagePath,
    FileStamp PackageStamp,
    IReadOnlyList<string> SourceIds,
    int Ordinal,
    long? ContentSize,
    PackageCompression Compression,
    string CompressionRaw);

/// <summary>
/// 候选冲突组：同一个 TGI 出现在至少两个不同 package 的索引里。
/// </summary>
/// <remarks>
/// 「候选」是字面意思。本阶段只比较索引，不比较资源内容；
/// 这些 package 里的资源可能完全相同（例如同一个文件的两份副本），
/// 也可能互相覆盖。是否构成真实冲突需要下一阶段读取 payload 后才能判断。
/// </remarks>
public sealed record PackageConflictCandidate(
    ResourceKey Key,
    IReadOnlyList<PackageResourceOccurrence> Occurrences)
{
    public int PackageCount => Occurrences
        .Select(occurrence => occurrence.PackagePath)
        .Distinct(PathRules.Comparer)
        .Count();
}

public enum PackageConflictScanIssueStage
{
    SourceValidation,
    Discovery,
    Metadata,
    IndexRead,
}

/// <summary>
/// 扫描问题。来源与发现阶段的问题沿用重复文件扫描的错误码；
/// 索引读取阶段的问题沿用 <see cref="PackageReadIssueCode"/>，
/// 并在 <paramref name="ReadStage"/> 里保留读取器给出的细分阶段。
/// </summary>
public sealed record PackageConflictScanIssue(
    string Code,
    PackageConflictScanIssueStage Stage,
    string Path,
    string Message,
    string? SourceId = null,
    PackageReadStage? ReadStage = null,
    string? Detail = null);

public static class PackageConflictScanIssueCode
{
    /// <summary>读取器抛出了契约之外的异常。该 package 计入未完成分析，扫描继续。</summary>
    public const string UnexpectedReadError = "package-read-unexpected-error";
}

/// <summary>
/// 候选冲突扫描结果。
/// </summary>
/// <param name="DiscoveredPackageCount">发现的 package 文件数（去重后）。</param>
/// <param name="AnalyzedPackageCount">索引读取成功、参与分组的 package 数。</param>
/// <param name="IncompletePackagePaths">
/// 索引读取失败、未参与分组的 package。它们里面的 TGI 不在结果中，
/// 因此「没有候选冲突」不代表这些文件没有冲突。
/// </param>
public sealed record PackageConflictScanReport(
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc,
    IReadOnlyList<ScanSourceSnapshot> Sources,
    int DiscoveredPackageCount,
    int AnalyzedPackageCount,
    IReadOnlyList<string> IncompletePackagePaths,
    IReadOnlyList<PackageConflictCandidate> Candidates,
    IReadOnlyList<PackageConflictScanIssue> Issues)
{
    public int IncompletePackageCount => IncompletePackagePaths.Count;
}

internal static class ReadOnlyLists
{
    public static IReadOnlyList<T> Freeze<T>(IEnumerable<T> items) =>
        new ReadOnlyCollection<T>(items.ToArray());
}
