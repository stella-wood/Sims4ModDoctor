using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Packages;

namespace Sims4ModSieve.Core.Conflicts;

/// <summary>
/// 资源内容读取在哪一步出的问题。
/// </summary>
public enum ResourceContentStage
{
    /// <summary>打不开文件、取不到文件戳。</summary>
    Access,

    /// <summary>文件在候选扫描之后或读取期间被改动。</summary>
    Stability,

    /// <summary>header 或索引读不出来，或者与候选扫描时的索引对不上。</summary>
    Index,

    /// <summary>读取存储数据：越界、截断、超出存储上限。</summary>
    Read,

    /// <summary>解压：不支持的压缩方式、数据损坏、长度不符、超出输出上限。</summary>
    Decompress,

    /// <summary>单次比较的共享预算用完。</summary>
    Budget,

    /// <summary>读取器抛出了契约之外的异常。</summary>
    Unexpected,
}

/// <summary>
/// 结构化的资源问题。<paramref name="Code"/> 是稳定错误码；
/// <paramref name="Message"/> 是给玩家看的安全摘要；<paramref name="Detail"/> 可选，承载技术细节。
/// </summary>
public sealed record ResourceContentIssue(
    string Code,
    ResourceContentStage Stage,
    string Message,
    string? Detail = null);

/// <summary>
/// 一条资源的内容哈希结果。成功时有哈希与实际解压字节数，失败时有问题；两者互斥。
/// </summary>
public sealed record ResourceContentResult
{
    private ResourceContentResult(
        ResourceContentTarget target,
        string? sha256,
        long? decompressedLength,
        ResourceContentIssue? issue)
    {
        Target = target;
        Sha256 = sha256;
        DecompressedLength = decompressedLength;
        Issue = issue;
    }

    public ResourceContentTarget Target { get; }

    /// <summary>解压后完整内容的 SHA-256，大写十六进制。</summary>
    public string? Sha256 { get; }

    /// <summary>实际解压输出的字节数，不是索引里的声明值。</summary>
    public long? DecompressedLength { get; }

    public ResourceContentIssue? Issue { get; }

    public bool IsSuccess => Sha256 is not null;

    public static ResourceContentResult Success(ResourceContentTarget target, string sha256, long decompressedLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        ArgumentOutOfRangeException.ThrowIfNegative(decompressedLength);
        return new ResourceContentResult(target, sha256, decompressedLength, issue: null);
    }

    public static ResourceContentResult Failure(ResourceContentTarget target, ResourceContentIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        return new ResourceContentResult(target, sha256: null, decompressedLength: null, issue);
    }
}

/// <summary>
/// 稳定错误码。字符串常量而非枚举，便于写入报告并保持跨版本可读。
/// </summary>
public static class ResourceContentIssueCode
{
    public const string AccessFailed = "resource-package-access-failed";
    public const string ChangedSinceScan = "resource-package-changed-since-scan";
    public const string ChangedDuringRead = "resource-package-changed-during-read";

    public const string IndexUnreadable = "resource-index-unreadable";
    public const string IndexMismatch = "resource-index-mismatch";

    public const string OutOfBounds = "resource-out-of-bounds";
    public const string StoredSizeExceedsLimit = "resource-stored-size-exceeds-limit";
    public const string Truncated = "resource-truncated";

    public const string UnsupportedCompression = "resource-unsupported-compression";
    public const string Deleted = "resource-deleted-record";
    public const string Corrupt = "resource-corrupt";
    public const string LengthMismatch = "resource-length-mismatch";
    public const string DecompressedSizeExceedsLimit = "resource-decompressed-size-exceeds-limit";

    public const string BudgetExhausted = "resource-comparison-budget-exhausted";

    /// <summary>读取器抛出了契约之外的异常，或返回了与请求对不上的结果。</summary>
    public const string UnexpectedError = "resource-read-unexpected-error";
}

/// <summary>
/// 候选组里一次资源出现的比较结果。
/// </summary>
/// <param name="PackageStamp">作为期望版本的文件戳（候选扫描时读到的），留给后续核对。</param>
public sealed record ResourceContentOccurrence(
    ResourceKey Key,
    string PackagePath,
    int Ordinal,
    FileStamp PackageStamp,
    string? Sha256,
    long? DecompressedLength,
    ResourceContentIssue? Issue)
{
    public bool IsSuccess => Sha256 is not null;
}

/// <summary>
/// 内容完全相同（解压后 SHA-256 相同）的一组资源出现。
/// </summary>
public sealed record ResourceContentSubgroup(
    string Sha256,
    long DecompressedLength,
    IReadOnlyList<ResourceContentOccurrence> Occurrences);

/// <summary>
/// 一个候选组的比较结论。
/// </summary>
public enum ResourceContentVerdict
{
    /// <summary>全部比较成功，且只有一种内容。</summary>
    Identical,

    /// <summary>
    /// 已经确认至少有两种不同内容。可能同时 <see cref="ResourceContentComparison.IsComplete"/> 为假：
    /// 差异已经确认，但还有资源没比较。
    /// </summary>
    Different,

    /// <summary>有资源没比较成功，现有结果不足以确认差异，也不能说内容相同。</summary>
    Incomplete,
}

/// <summary>
/// 一个候选冲突组（同一个 TGI）的内容比较结果。
/// </summary>
/// <remarks>
/// 相同 TGI、内容不同，表示这些 package 对同一条资源给出了不同版本——即「资源覆盖差异」。
/// 它不代表一定会导致游戏故障：很多覆盖是作者刻意制作的。本结果不判断加载顺序、
/// 最终生效的是哪一个文件，也不给出严重度或删除建议。
/// </remarks>
/// <param name="ContentGroups">比较成功的资源，按内容哈希划分。</param>
/// <param name="Uncompared">没能比较的资源及原因。</param>
public sealed record ResourceContentComparison(
    ResourceKey Key,
    IReadOnlyList<ResourceContentSubgroup> ContentGroups,
    IReadOnlyList<ResourceContentOccurrence> Uncompared)
{
    /// <summary>候选组里每一条资源都比较成功。</summary>
    public bool IsComplete => Uncompared.Count == 0;

    /// <summary>已经确认至少存在两种不同内容，不受其他资源失败影响。</summary>
    public bool HasConfirmedDifference => ContentGroups.Count >= 2;

    public ResourceContentVerdict Verdict =>
        HasConfirmedDifference ? ResourceContentVerdict.Different
        : IsComplete && ContentGroups.Count == 1 && ContentGroups[0].Occurrences.Count >= 2
            ? ResourceContentVerdict.Identical
            : ResourceContentVerdict.Incomplete;
}

/// <summary>
/// 参与比较的 package 版本，留给后续核对：扫描时的文件戳与读取结束后观察到的文件戳。
/// </summary>
/// <param name="StampAfterRead">读取结束后的文件戳；没读到（例如打不开）时为 <see langword="null"/>。</param>
/// <param name="IsStable">读取前后文件戳都与扫描时一致。</param>
public sealed record ResourceContentPackageVersion(
    string Path,
    FileStamp ScannedStamp,
    FileStamp? StampAfterRead,
    bool IsStable);

public sealed record ResourceContentComparisonRequest(
    IReadOnlyList<PackageConflictCandidate> Candidates,
    ResourceContentLimits? Limits = null);

public enum ResourceContentComparisonPhase
{
    Starting,
    ReadingContents,
    Grouping,
    Completed,
}

public sealed record ResourceContentComparisonProgress(
    ResourceContentComparisonPhase Phase,
    string Message,
    int CompletedResources,
    int TotalResources,
    long ProcessedBytes);

/// <summary>
/// 内容比较报告。
/// </summary>
/// <param name="ProcessedBytes">实际读取与解压输出的字节总数（共享预算里扣掉的量）。</param>
public sealed record ResourceContentComparisonReport(
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc,
    ResourceContentLimits Limits,
    IReadOnlyList<ResourceContentComparison> Comparisons,
    IReadOnlyList<ResourceContentPackageVersion> Packages,
    long ProcessedBytes);
