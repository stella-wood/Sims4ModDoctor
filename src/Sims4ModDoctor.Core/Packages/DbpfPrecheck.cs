using System.Buffers.Binary;
using System.Numerics;

namespace Sims4ModDoctor.Core.Packages;

/// <summary>
/// 交给第三方库之前的轻量结构预检。
/// </summary>
/// <remarks>
/// 存在的理由只有一个：第三方库可能在自己的安全检查之前，就按文件里声明的
/// 条目数分配内存。所以「文件说它有多少条资源」这个数字，必须在库看到它之前
/// 先被本项目验一遍。
/// <para>
/// 本类型只做能从 header 与索引头确定的判断，不解析记录、不读取 payload、
/// 不解压，也不构成完整的 DBPF 解析器。
/// </para>
/// </remarks>
public static class DbpfPrecheck
{
    /// <summary>DBPF header 固定 96 字节。</summary>
    public const int HeaderLength = 96;

    /// <summary>索引开头的 indexType 位域本身占一个 DWORD。</summary>
    public const int IndexTypeFieldLength = 4;

    /// <summary>一条逻辑索引记录固定 8 个 DWORD，其中被声明为公共常量的字段不再逐条存储。</summary>
    public const int IndexFieldsPerEntry = 8;

    /// <summary>已知 indexType 位域只使用低 8 位，每一位对应一个可提取为公共常量的字段。</summary>
    public const uint KnownIndexTypeMask = 0xFF;

    private const uint Magic = 0x46504244; // "DBPF"，小端

    /// <summary>本工具支持的 Sims 4 package 版本，实测样本为 2.1。</summary>
    public const int SupportedMajorVersion = 2;

    /// <summary>次版本必须精确匹配：Sims 2 的包是 1.1，按「大于等于 2」判断会放进不该支持的文件。</summary>
    public const int SupportedMinorVersion = 1;

    /// <summary>索引布局版本，实测恒为 3。</summary>
    public const int SupportedIndexVersion = 3;

    private const int OffsetMagic = 0x00;
    private const int OffsetMajor = 0x04;
    private const int OffsetMinor = 0x08;
    private const int OffsetEntryCount = 0x24;
    private const int OffsetIndexSize = 0x2C;
    private const int OffsetIndexVersion = 0x3C;
    private const int OffsetIndexPosition = 0x40;

    /// <summary>
    /// header 里本阶段用得到的字段。字段本身是无符号的，这里保留原始 <see cref="uint"/>，
    /// 由调用方负责范围检查，避免在解析阶段就悄悄截断。
    /// </summary>
    public readonly record struct DbpfHeader(
        uint Major,
        uint Minor,
        uint EntryCount,
        uint IndexSize,
        uint IndexVersion,
        uint IndexPosition);

    /// <summary>
    /// 校验 header。<paramref name="header"/> 必须至少 <see cref="HeaderLength"/> 字节。
    /// </summary>
    public static PackageReadIssue? ValidateHeader(
        ReadOnlySpan<byte> header,
        long fileLength,
        string path,
        PackageSafetyLimits limits,
        out DbpfHeader parsed)
    {
        ArgumentNullException.ThrowIfNull(limits);
        parsed = default;

        if (header.Length < HeaderLength)
        {
            return TooSmall(path, fileLength);
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(header[OffsetMagic..]) != Magic)
        {
            return new PackageReadIssue(
                PackageReadIssueCode.MagicMismatch,
                PackageReadStage.Precheck,
                path,
                "这个文件不是 package 格式。",
                "文件开头不是 DBPF 标记。");
        }

        var major = BinaryPrimitives.ReadUInt32LittleEndian(header[OffsetMajor..]);
        var minor = BinaryPrimitives.ReadUInt32LittleEndian(header[OffsetMinor..]);

        if (major != SupportedMajorVersion || minor != SupportedMinorVersion)
        {
            return new PackageReadIssue(
                PackageReadIssueCode.UnsupportedVersion,
                PackageReadStage.Precheck,
                path,
                "这个 package 的版本本工具还不支持。",
                $"读到 DBPF {major}.{minor}，只支持 "
                    + $"{SupportedMajorVersion}.{SupportedMinorVersion}。");
        }

        var indexVersion = BinaryPrimitives.ReadUInt32LittleEndian(header[OffsetIndexVersion..]);
        if (indexVersion != SupportedIndexVersion)
        {
            return new PackageReadIssue(
                PackageReadIssueCode.UnsupportedIndexVersion,
                PackageReadStage.Precheck,
                path,
                "这个 package 的索引格式本工具还不支持。",
                $"读到索引版本 {indexVersion}，只支持 {SupportedIndexVersion}。");
        }

        var entryCount = BinaryPrimitives.ReadUInt32LittleEndian(header[OffsetEntryCount..]);
        var indexSize = BinaryPrimitives.ReadUInt32LittleEndian(header[OffsetIndexSize..]);
        var indexPosition = BinaryPrimitives.ReadUInt32LittleEndian(header[OffsetIndexPosition..]);

        parsed = new DbpfHeader(major, minor, entryCount, indexSize, indexVersion, indexPosition);

        if (entryCount > (uint)limits.MaxResourceCount)
        {
            return new PackageReadIssue(
                PackageReadIssueCode.ResourceCountExceedsLimit,
                PackageReadStage.Precheck,
                path,
                "这个 package 声称包含的资源数量异常，已停止分析。",
                $"声明 {entryCount} 条，上限 {limits.MaxResourceCount} 条。");
        }

        if (entryCount == 0)
        {
            // 空索引的合法形态是三个字段同时为零；只清空其中一个通常意味着文件被截断或改写过。
            return indexSize == 0 && indexPosition == 0
                ? null
                : new PackageReadIssue(
                    PackageReadIssueCode.IndexOutOfBounds,
                    PackageReadStage.Precheck,
                    path,
                    "这个 package 的索引信息自相矛盾。",
                    $"资源数为 0，但索引大小 {indexSize}、位置 {indexPosition} 不为 0。");
        }

        if (indexPosition < HeaderLength || indexSize < IndexTypeFieldLength)
        {
            return new PackageReadIssue(
                PackageReadIssueCode.IndexOutOfBounds,
                PackageReadStage.Precheck,
                path,
                "这个 package 的索引位置不合法。",
                $"索引位置 {indexPosition}、大小 {indexSize}；"
                    + $"位置至少应为 {HeaderLength}，大小至少应为 {IndexTypeFieldLength}。");
        }

        // 两个字段都是 uint32，先各自提升到 long 再相加，避免 32 位回绕后落进合法区间。
        var indexEnd = (long)indexPosition + indexSize;
        if (indexEnd > fileLength)
        {
            return new PackageReadIssue(
                PackageReadIssueCode.IndexOutOfBounds,
                PackageReadStage.Precheck,
                path,
                "这个 package 的索引超出了文件范围，文件可能已损坏或被截断。",
                $"索引结束于 {indexEnd}，文件长度 {fileLength}。");
        }

        return null;
    }

    /// <summary>
    /// 校验索引头。位域决定每条记录在磁盘上实际占几个 DWORD，
    /// 因此 <paramref name="indexSize"/> 是可以被算出来的，而不只是被信任的。
    /// </summary>
    public static PackageReadIssue? ValidateIndexShape(
        uint indexType,
        uint entryCount,
        uint indexSize,
        string path)
    {
        if ((indexType & ~KnownIndexTypeMask) != 0)
        {
            return new PackageReadIssue(
                PackageReadIssueCode.UnsupportedIndexVersion,
                PackageReadStage.Precheck,
                path,
                "这个 package 的索引使用了本工具不认识的布局。",
                $"indexType 位域 0x{indexType:X8} 含已知 8 位之外的置位。");
        }

        // 索引头 = 位域本身 + 每个置位对应的一个公共常量 DWORD；
        // 之后每条记录只存剩下的字段。漏掉中间那段公共常量，
        // 在位域为 0 的样本上依然算得对 —— 而真实 mod 的位域几乎不会是 0。
        var constantFieldCount = BitOperations.PopCount(indexType);
        var fieldsPerEntry = IndexFieldsPerEntry - constantFieldCount;
        var indexHeaderLength = IndexTypeFieldLength + (constantFieldCount * sizeof(uint));
        var expected = indexHeaderLength + ((long)entryCount * fieldsPerEntry * sizeof(uint));

        if (expected != indexSize)
        {
            return new PackageReadIssue(
                PackageReadIssueCode.IndexSizeMismatch,
                PackageReadStage.Precheck,
                path,
                "这个 package 的索引长度与它声明的资源数量对不上，文件可能已损坏或被截断。",
                $"位域 0x{indexType:X8} 提取了 {constantFieldCount} 个公共字段，"
                    + $"索引头 {indexHeaderLength} 字节加 {entryCount} 条记录共应占 {expected} 字节，"
                    + $"header 声明 {indexSize} 字节。");
        }

        return null;
    }

    internal static PackageReadIssue TooSmall(string path, long fileLength) =>
        new(PackageReadIssueCode.TooSmallForHeader,
            PackageReadStage.Precheck,
            path,
            "这个文件太小，不可能是一个 package。",
            $"文件长度 {fileLength} 字节，header 需要 {HeaderLength} 字节。");
}
