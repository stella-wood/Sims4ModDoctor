using Sims4ModDoctor.Core.Duplicates;

namespace Sims4ModDoctor.Core.Packages;

/// <summary>
/// DBPF 资源键。Type 与 Group 为 uint32，Instance 为 uint64。
/// </summary>
public readonly record struct ResourceKey(uint Type, uint Group, ulong Instance)
{
    /// <summary>
    /// s4pe 与社区工具通用的 TGI 文本形式，便于与外部工具逐项对拍。
    /// </summary>
    public string Tgi => $"{Type:X8}:{Group:X8}:{Instance:X16}";

    public override string ToString() => Tgi;
}

/// <summary>
/// 第三方库可靠提供的压缩信息。未知取值一律归入 <see cref="Unknown"/>，
/// 不猜测其含义，也不据此推断内容是否可解压。
/// </summary>
public enum PackageCompression
{
    Unknown,
    None,
    Zlib,
    RefPack,
    Deleted,
}

/// <summary>
/// 索引中的一条资源记录。本阶段只读索引，不读取也不解压 payload。
/// </summary>
/// <param name="ContentSize">
/// 第三方库报告的资源内容大小；<see langword="null"/> 表示这一项取不到。
/// ⚠️ 取这个值需要库触碰资源内容，不是纯索引操作，因此允许缺失：
/// 一条元数据拿不到，不应让整个 package 的索引读取失败。
/// 该值尚未与 s4pe 的 memory size 逐项对拍。
/// </param>
/// <param name="CompressionRaw">
/// 第三方库给出的压缩模式原文。<see cref="Compression"/> 是它的规范化映射，
/// 遇到不认识的取值会归入 <see cref="PackageCompression.Unknown"/>，
/// 原文则原样保留，便于事后核对。
/// </param>
public sealed record PackageResourceEntry(
    ResourceKey Key,
    int Ordinal,
    long? ContentSize,
    PackageCompression Compression,
    string CompressionRaw);

/// <summary>
/// 一个 package 的只读索引快照。
/// </summary>
public sealed record PackageIndexSummary(
    string Path,
    int DbpfMajorVersion,
    int DbpfMinorVersion,
    long FileSize,
    FileStamp Stamp,
    IReadOnlyList<PackageResourceEntry> Resources)
{
    public int ResourceCount => Resources.Count;

    public string DbpfVersion => $"{DbpfMajorVersion}.{DbpfMinorVersion}";
}

/// <summary>
/// 读取失败的阶段。与 <see cref="Duplicates.ScanIssueStage"/> 分开，
/// 因为两者的排查动作不同。
/// </summary>
public enum PackageReadStage
{
    Access,
    Precheck,
    Index,
    Stability,
}

/// <summary>
/// 结构化的读取问题。<paramref name="Code"/> 是稳定错误码，可用于测试与报告匹配；
/// <paramref name="Message"/> 是给玩家看的安全摘要，不含内部路径推断与栈信息；
/// <paramref name="Detail"/> 可选，承载技术细节。
/// </summary>
public sealed record PackageReadIssue(
    string Code,
    PackageReadStage Stage,
    string Path,
    string Message,
    string? Detail = null);

/// <summary>
/// 读取结果。成功时携带索引摘要，失败时携带结构化问题；两者互斥。
/// </summary>
public sealed record PackageReadResult
{
    private PackageReadResult(PackageIndexSummary? summary, PackageReadIssue? issue)
    {
        Summary = summary;
        Issue = issue;
    }

    public PackageIndexSummary? Summary { get; }

    public PackageReadIssue? Issue { get; }

    public bool IsSuccess => Summary is not null;

    public static PackageReadResult Success(PackageIndexSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return new PackageReadResult(summary, issue: null);
    }

    public static PackageReadResult Failure(PackageReadIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        return new PackageReadResult(summary: null, issue);
    }
}

/// <summary>
/// 稳定错误码。字符串常量而非枚举，便于写入报告并保持跨版本可读。
/// </summary>
public static class PackageReadIssueCode
{
    public const string PathInvalid = "package-path-invalid";
    public const string NotFound = "package-not-found";
    public const string AccessFailed = "package-access-failed";

    public const string TooSmallForHeader = "package-too-small-for-header";
    public const string TooLarge = "package-too-large";
    public const string MagicMismatch = "package-magic-mismatch";
    public const string UnsupportedVersion = "package-unsupported-version";
    public const string UnsupportedIndexVersion = "package-unsupported-index-version";
    public const string IndexOutOfBounds = "package-index-out-of-bounds";
    public const string IndexSizeMismatch = "package-index-size-mismatch";
    public const string ResourceCountExceedsLimit = "package-resource-count-exceeds-limit";

    public const string IndexReadFailed = "package-index-read-failed";
    public const string DuplicateResourceKeys = "package-duplicate-resource-keys";
    public const string ChangedDuringRead = "package-changed-during-read";
}
