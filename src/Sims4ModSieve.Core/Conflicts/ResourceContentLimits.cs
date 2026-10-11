namespace Sims4ModSieve.Core.Conflicts;

/// <summary>
/// 资源内容比较的安全上限。全部按实际读取 / 实际输出的字节执行；
/// 索引里的声明值只用于提前拒绝，不作为放行依据。
/// </summary>
/// <param name="MaxStoredBytesPerResource">单条资源在 package 里存储数据（压缩后）的读取上限。</param>
/// <param name="MaxDecompressedBytesPerResource">单条资源解压输出的上限。</param>
/// <param name="MaxTotalProcessedBytes">
/// 单次比较的总处理预算：所有资源的读取字节与解压输出字节之和。
/// 由所有并发任务共享，而不是每个任务各有一份。默认不限。
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
    /// 默认不限总量。内容是流式哈希的，总量只影响耗时、不影响内存，耗时由用户取消控制；
    /// 防坏文件和解压炸弹靠上面两个单条上限。原来的 4 GiB 在真实 Mods 文件夹上
    /// 会在比较中途耗尽，把后面的资源全部标成未完成。
    /// </summary>
    public const long DefaultMaxTotalProcessedBytes = long.MaxValue;

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
    private readonly object _gate = new();
    private long _consumed;
    private long _reserved;
    private TaskCompletionSource _budgetChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ResourceContentBudget(long totalBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(totalBytes, 1);
        TotalBytes = totalBytes;
    }

    public long TotalBytes { get; }

    public long ConsumedBytes { get { lock (_gate) return _consumed; } }

    /// <summary>
    /// 尝试扣除 <paramref name="bytes"/>。余额不足时不扣、返回 <see langword="false"/>。
    /// </summary>
    public bool TryConsume(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        lock (_gate)
        {
            if (bytes > TotalBytes - _consumed - _reserved)
            {
                return false;
            }
            _consumed += bytes;
            return true;
        }
    }

    /// <summary>
    /// 在 IO 或解压前预留最多 maxBytes 字节；额度不足时允许缩小本次缓冲。
    /// bytesPerUnit=2 用于未压缩数据，同时计入读取和内容输出。
    /// 未使用的额度在 Complete 或 Dispose 时退还，已结算量只增不减。
    /// </summary>
    public Reservation ReserveUpTo(int maxBytes, int bytesPerUnit = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(bytesPerUnit, 1);
        lock (_gate)
        {
            var granted = (int)Math.Min(maxBytes, (TotalBytes - _consumed - _reserved) / bytesPerUnit);
            var reservation = new Reservation(this, granted, bytesPerUnit);
            _reserved += (long)granted * bytesPerUnit;
            return reservation;
        }
    }

    /// <summary>其他任务临时占用全部余额时，等待结算而不是误报预算耗尽。</summary>
    public async ValueTask<Reservation> ReserveUpToAsync(
        int maxBytes, int bytesPerUnit = 1, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(bytesPerUnit, 1);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task changed;
            lock (_gate)
            {
                if (maxBytes == 0 || _reserved == 0 || TotalBytes - _consumed - _reserved >= bytesPerUnit)
                {
                    return ReserveUpTo(maxBytes, bytesPerUnit);
                }
                changed = _budgetChanged.Task;
            }
            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void NotifySettlement()
    {
        var changed = _budgetChanged;
        _budgetChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        changed.TrySetResult();
    }

    public sealed class Reservation : IDisposable
    {
        private readonly ResourceContentBudget _owner;
        private readonly int _bytesPerUnit;
        private bool _settled;

        internal Reservation(ResourceContentBudget owner, int grantedBytes, int bytesPerUnit)
        {
            _owner = owner;
            GrantedBytes = grantedBytes;
            _bytesPerUnit = bytesPerUnit;
        }

        public int GrantedBytes { get; }

        public void Complete(int actualBytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(actualBytes);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(actualBytes, GrantedBytes);
            lock (_owner._gate)
            {
                if (_settled) throw new InvalidOperationException("Reservation already settled.");
                _owner._reserved -= (long)GrantedBytes * _bytesPerUnit;
                _owner._consumed += (long)actualBytes * _bytesPerUnit;
                _settled = true;
                _owner.NotifySettlement();
            }
        }

        public void Dispose()
        {
            lock (_owner._gate)
            {
                if (_settled) return;
                _owner._reserved -= (long)GrantedBytes * _bytesPerUnit;
                _settled = true;
                _owner.NotifySettlement();
            }
        }
    }
}
