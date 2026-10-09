using System.Collections.Concurrent;
using Sims4ModDoctor.Core.Conflicts;
using Sims4ModDoctor.Core.Duplicates;
using Sims4ModDoctor.Core.Packages;

namespace Sims4ModDoctor.Core.Tests.Conflicts;

/// <summary>
/// 按路径与 ordinal 返回预设哈希的读取器。不打开文件，只让 Core 的比较逻辑可以单独测试。
/// </summary>
internal sealed class FakeResourceContentHasher : IResourceContentHasher
{
    private readonly Dictionary<(string Path, int Ordinal), (string Sha256, long Length)> _contents = new();
    private readonly Dictionary<(string Path, int Ordinal), ResourceContentIssue> _failures = new();
    private readonly Dictionary<string, Exception> _throws = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TimeSpan> _delays = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<ResourceContentBatchRequest> _requests = new();
    private readonly ConcurrentBag<ResourceContentBudget> _budgets = [];
    private int _inFlight;
    private int _maxInFlight;

    public IReadOnlyCollection<ResourceContentBatchRequest> Requests => _requests;

    public IReadOnlyCollection<ResourceContentBudget> Budgets => _budgets;

    public int MaxInFlight => _maxInFlight;

    /// <summary>每批开始时调用；测试用它在读取中途触发取消。</summary>
    public Action<string>? OnHash { get; set; }

    /// <summary>非空时，每条成功资源从预算里扣这么多字节；扣不动就返回预算失败。</summary>
    public long? BytesPerResource { get; set; }

    /// <summary>为真时返回的结果条数少一条，模拟违反契约的读取器。</summary>
    public bool DropLastResult { get; set; }

    public FakeResourceContentHasher WithContent(string path, int ordinal, string sha256, long length = 4)
    {
        _contents[(path, ordinal)] = (sha256, length);
        return this;
    }

    public FakeResourceContentHasher WithFailure(
        string path,
        int ordinal,
        string code = ResourceContentIssueCode.Corrupt,
        ResourceContentStage stage = ResourceContentStage.Decompress)
    {
        _failures[(path, ordinal)] = new ResourceContentIssue(code, stage, "测试用失败");
        return this;
    }

    public FakeResourceContentHasher WithException(string path, Exception exception)
    {
        _throws[path] = exception;
        return this;
    }

    public FakeResourceContentHasher WithDelay(string path, TimeSpan delay)
    {
        _delays[path] = delay;
        return this;
    }

    public async Task<ResourceContentBatchResult> HashAsync(
        ResourceContentBatchRequest request,
        ResourceContentBudget budget,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _requests.Enqueue(request);
        _budgets.Add(budget);

        var current = Interlocked.Increment(ref _inFlight);
        InterlockedMax(ref _maxInFlight, current);
        try
        {
            OnHash?.Invoke(request.PackagePath);
            if (_delays.TryGetValue(request.PackagePath, out var delay))
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (_throws.TryGetValue(request.PackagePath, out var exception))
            {
                throw exception;
            }

            var results = request.Targets.Select(target => Resolve(request.PackagePath, target, budget)).ToList();
            if (DropLastResult && results.Count > 0)
            {
                results.RemoveAt(results.Count - 1);
            }

            return new ResourceContentBatchResult(request.PackagePath, request.ExpectedStamp, results);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private ResourceContentResult Resolve(string path, ResourceContentTarget target, ResourceContentBudget budget)
    {
        if (_failures.TryGetValue((path, target.Ordinal), out var issue))
        {
            return ResourceContentResult.Failure(target, issue);
        }

        if (!_contents.TryGetValue((path, target.Ordinal), out var content))
        {
            throw new InvalidOperationException($"测试没有为 {path}#{target.Ordinal} 准备内容。");
        }

        if (BytesPerResource is { } bytes && !budget.TryConsume(bytes))
        {
            return ResourceContentResult.Failure(target, new ResourceContentIssue(
                ResourceContentIssueCode.BudgetExhausted,
                ResourceContentStage.Budget,
                "测试用预算耗尽"));
        }

        return ResourceContentResult.Success(target, content.Sha256, content.Length);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value
            && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}

/// <summary>手工拼候选组，不经过索引扫描。</summary>
internal static class CandidateFactory
{
    public static readonly FileStamp Stamp = new(1024, new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc));

    public static ResourceKey Key(uint instance = 1) => new(0x034AEECB, 0, instance);

    public static PackageConflictCandidate Candidate(ResourceKey key, params (string Path, int Ordinal)[] occurrences) =>
        new(key, occurrences
            .Select(occurrence => new PackageResourceOccurrence(
                key,
                occurrence.Path,
                Stamp,
                ["mods"],
                occurrence.Ordinal,
                4,
                PackageCompression.Zlib,
                "ForceZLib"))
            .ToArray());
}
