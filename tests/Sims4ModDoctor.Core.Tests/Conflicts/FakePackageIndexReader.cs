using System.Collections.Concurrent;
using Sims4ModDoctor.Core.Duplicates;
using Sims4ModDoctor.Core.Packages;

namespace Sims4ModDoctor.Core.Tests.Conflicts;

/// <summary>
/// 按文件名返回预设索引的读取器。不打开文件，只让 Core 的扫描逻辑可以脱离第三方库单独测试。
/// </summary>
internal sealed class FakePackageIndexReader : IPackageIndexReader
{
    private readonly Dictionary<string, ResourceKey[]> _indexes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PackageReadIssue> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Exception> _throws = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TimeSpan> _delays = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _readCounts = new(StringComparer.OrdinalIgnoreCase);
    private int _inFlight;
    private int _maxInFlight;

    public IReadOnlyDictionary<string, int> ReadCounts => _readCounts;

    public int MaxInFlight => _maxInFlight;

    /// <summary>每次读取开始时调用；测试用它在读取中途触发取消。</summary>
    public Action<string>? OnRead { get; set; }

    public FakePackageIndexReader WithIndex(string fileName, params ResourceKey[] keys)
    {
        _indexes[fileName] = keys;
        return this;
    }

    public FakePackageIndexReader WithFailure(string fileName, string code = PackageReadIssueCode.MagicMismatch)
    {
        _failures[fileName] = new PackageReadIssue(code, PackageReadStage.Precheck, fileName, "测试用失败");
        return this;
    }

    public FakePackageIndexReader WithException(string fileName, Exception exception)
    {
        _throws[fileName] = exception;
        return this;
    }

    public FakePackageIndexReader WithDelay(string fileName, TimeSpan delay)
    {
        _delays[fileName] = delay;
        return this;
    }

    public async Task<PackageReadResult> ReadIndexAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = Path.GetFileName(packagePath);
        _readCounts.AddOrUpdate(packagePath, 1, (_, count) => count + 1);

        var current = Interlocked.Increment(ref _inFlight);
        InterlockedMax(ref _maxInFlight, current);
        try
        {
            OnRead?.Invoke(name);

            if (_delays.TryGetValue(name, out var delay))
            {
                await Task.Delay(delay, cancellationToken);
            }
            else
            {
                await Task.Yield();
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (_throws.TryGetValue(name, out var exception))
            {
                throw exception;
            }

            if (_failures.TryGetValue(name, out var issue))
            {
                return PackageReadResult.Failure(issue with { Path = packagePath });
            }

            var keys = _indexes.GetValueOrDefault(name) ?? [];
            var entries = keys
                .Select((key, ordinal) => new PackageResourceEntry(
                    key,
                    ordinal,
                    ContentSize: 100 + ordinal,
                    PackageCompression.Zlib,
                    "ZLIB"))
                .ToArray();
            return PackageReadResult.Success(new PackageIndexSummary(
                packagePath,
                2,
                1,
                FileSize: 1,
                new FileStamp(1, DateTime.UnixEpoch),
                entries));
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int snapshot;
        do
        {
            snapshot = target;
            if (value <= snapshot)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, snapshot) != snapshot);
    }
}

/// <summary>把目录枚举结果倒过来，用来验证结果不依赖文件系统返回顺序。</summary>
internal sealed class ReversedEnumerationFileSystem(IFileSystemAccess inner) : IFileSystemAccess
{
    public bool DirectoryExists(string path) => inner.DirectoryExists(path);

    public IReadOnlyList<string> EnumerateFileSystemEntries(string directory) =>
        inner.EnumerateFileSystemEntries(directory).Reverse().ToArray();

    public FileAttributes GetAttributes(string path) => inner.GetAttributes(path);

    public FileStamp GetFileStamp(string path) => inner.GetFileStamp(path);

    public Stream OpenRead(string path) => inner.OpenRead(path);
}
