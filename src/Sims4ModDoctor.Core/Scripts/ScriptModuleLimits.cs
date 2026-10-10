namespace Sims4ModDoctor.Core.Scripts;

/// <summary>
/// 脚本归档分析的安全上限。.ts4script 来自网上，按不可信输入处理：
/// 有界遍历中央目录，在分配下一条记录前检查实际条目数量，并核对尾部声明；
/// 条目内容按实际解压输出的字节计数，不信任 zip 里声明的长度。
/// </summary>
/// <param name="MaxArchiveBytes">单个 .ts4script 文件的体积上限。</param>
/// <param name="MaxEntriesPerArchive">单个归档的条目数上限（含目录与非模块文件）。</param>
/// <param name="MaxModuleBytes">单个模块条目解压输出的上限。</param>
/// <param name="MaxTotalProcessedBytes">单次扫描所有模块解压输出之和的上限，由并发任务共享。</param>
/// <param name="MaxDegreeOfParallelism">同时处理的归档数上限。</param>
public sealed record ScriptModuleLimits(
    long MaxArchiveBytes = ScriptModuleLimits.DefaultMaxArchiveBytes,
    int MaxEntriesPerArchive = ScriptModuleLimits.DefaultMaxEntriesPerArchive,
    long MaxModuleBytes = ScriptModuleLimits.DefaultMaxModuleBytes,
    long MaxTotalProcessedBytes = ScriptModuleLimits.DefaultMaxTotalProcessedBytes,
    int MaxDegreeOfParallelism = ScriptModuleLimits.DefaultMaxDegreeOfParallelism)
{
    /// <summary>
    /// 256 MiB。常见脚本 mod 在几 KiB 到几 MiB（样本里最大的约 3.7 MiB、一千一百多个模块）；
    /// 大出两个数量级的 .ts4script 更可能是损坏或改了扩展名的别的东西。
    /// </summary>
    public const long DefaultMaxArchiveBytes = 256L * 1024 * 1024;

    /// <summary>
    /// 65535。普通 zip 中央目录尾部的条目数字段只有 16 位，再多就必须是 Zip64，
    /// 而本分析器不接受 Zip64（见 <see cref="ScriptArchiveIssueCode.Zip64Unsupported"/>）。
    /// </summary>
    public const int DefaultMaxEntriesPerArchive = ushort.MaxValue;

    /// <summary>
    /// 16 MiB。编译后的单个 Python 模块通常在几 KiB 到几百 KiB；
    /// 这个值限制的是「解压炸弹」与处理时间，内容是流式计算哈希的，不整份进内存。
    /// </summary>
    public const long DefaultMaxModuleBytes = 16L * 1024 * 1024;

    /// <summary>1 GiB。远超常见 Mods 文件夹里全部脚本解压后的总量。</summary>
    public const long DefaultMaxTotalProcessedBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>2。以顺序读磁盘为主，与内容比较保持一致。</summary>
    public const int DefaultMaxDegreeOfParallelism = 2;

    public static ScriptModuleLimits Default { get; } = new();

    /// <summary>非法配置直接抛出，不在扫描中途才发现。</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxArchiveBytes, 22, nameof(MaxArchiveBytes));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxEntriesPerArchive, 1, nameof(MaxEntriesPerArchive));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxModuleBytes, 1, nameof(MaxModuleBytes));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTotalProcessedBytes, 1, nameof(MaxTotalProcessedBytes));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDegreeOfParallelism, 1, nameof(MaxDegreeOfParallelism));
    }
}
