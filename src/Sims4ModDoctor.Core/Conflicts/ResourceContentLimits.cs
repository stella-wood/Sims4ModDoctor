namespace Sims4ModDoctor.Core.Conflicts;

/// <summary>
/// 资源内容比较的安全上限。全部按实际读取 / 实际输出的字节执行；
/// 索引里的声明值只用于提前拒绝，不作为放行依据。
/// </summary>
/// <param name="MaxStoredBytesPerResource">单条资源在 package 里存储数据（压缩后）的读取上限。</param>
/// <param name="MaxDecompressedBytesPerResource">单条资源解压输出的上限。</param>
/// <param name="MaxTotalProcessedBytes">
/// 单次比较的总处理预算：所有资源的读取字节与解压输出字节之和。
/// 由所有并发任务共享，而不是每个任务各有一份。
/// </param>
/// <param name="MaxDegreeOfParallelism">同时处理的 package 数上限。</param>
public sealed record ResourceContentLimits(
    long MaxStoredBytesPerResource = ResourceContentLimits.DefaultMaxStoredBytesPerResource,
    long MaxDecompressedBytesPerResource = ResourceContentLimits.DefaultMaxDecompressedBytesPerResource,
    long MaxTotalProcessedBytes = ResourceContentLimits.DefaultMaxTotalProcessedBytes,
    int MaxDegreeOfParallelism = ResourceContentLimits.DefaultMaxDegreeOfParallelism)
{
    /// <summary>
    /// 64 MiB。Sims 4 资源里最大的一类是贴图与网格，常见在几十 KiB 到十几 MiB；
    /// 存储数据超过 64 MiB 的单条资源更可能是损坏或恶意构造，而不是正常内容。
    /// </summary>
    public const long DefaultMaxStoredBytesPerResource = 64L * 1024 * 1024;

    /// <summary>
    /// 256 MiB。解压后通常比存储数据大几倍，所以比存储上限宽；
    /// 内容是流式计算哈希的，这个值限制的是处理时间与「解压炸弹」，不是内存占用。
    /// </summary>
    public const long DefaultMaxDecompressedBytesPerResource = 256L * 1024 * 1024;

    /// <summary>
    /// 4 GiB。足以覆盖上千组普通候选冲突；一次比较读写超过这个量，
    /// 更可能是输入异常或范围选得过大，应停下来交给用户缩小范围。
    /// </summary>
    public const long DefaultMaxTotalProcessedBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>
    /// 2。比较以顺序读磁盘为主，解压与哈希的 CPU 开销不大；
    /// 并发过高只会让机械硬盘来回寻道。
    /// </summary>
    public const int DefaultMaxDegreeOfParallelism = 2;

    public static ResourceContentLimits Default { get; } = new();

    /// <summary>非法配置直接抛出，不在比较中途才发现。</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxStoredBytesPerResource, 1, nameof(MaxStoredBytesPerResource));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDecompressedBytesPerResource, 1, nameof(MaxDecompressedBytesPerResource));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTotalProcessedBytes, 1, nameof(MaxTotalProcessedBytes));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDegreeOfParallelism, 1, nameof(MaxDegreeOfParallelism));
    }
}

/// <summary>
/// 单次比较共享的处理预算。所有并发任务都从同一个计数里扣，
/// 扣不动就失败，不会出现「每个任务各花一份完整额度」。
/// </summary>
public sealed class ResourceContentBudget
{
    private long _consumed;

    public ResourceContentBudget(long totalBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(totalBytes, 1);
        TotalBytes = totalBytes;
    }

    public long TotalBytes { get; }

    public long ConsumedBytes => Volatile.Read(ref _consumed);

    /// <summary>
    /// 尝试扣除 <paramref name="bytes"/>。余额不足时不扣、返回 <see langword="false"/>。
    /// </summary>
    public bool TryConsume(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        while (true)
        {
            var current = Volatile.Read(ref _consumed);
            var next = current + bytes;
            if (next > TotalBytes || next < current)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _consumed, next, current) == current)
            {
                return true;
            }
        }
    }
}
