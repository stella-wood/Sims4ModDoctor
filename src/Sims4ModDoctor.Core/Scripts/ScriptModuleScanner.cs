using Sims4ModDoctor.Core.Conflicts;
using Sims4ModDoctor.Core.Duplicates;

namespace Sims4ModDoctor.Core.Scripts;

public interface IScriptModuleScanner
{
    Task<ScriptModuleScanReport> ScanAsync(
        ScriptModuleScanRequest request,
        IProgress<ScriptModuleScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 脚本模块碰撞扫描：列出每个 .ts4script 里可 import 的模块，找出出现在两个以上归档里的模块名，
/// 并按内容分成相同 / 不同 / 没比完。
/// </summary>
/// <remarks>
/// <para>只读取 zip 结构与条目字节，不执行、不反编译任何代码。
/// 脚本模块和 package 资源是两套东西，碰撞结果不混入 TGI 模型。</para>
/// <para>每个归档只打开一次：列模块和计算内容哈希在同一次读取里完成，
/// 读取前后核对文件戳，中途被改动则整个归档的结果作废。代价是不碰撞的模块也会被读一遍；
/// 脚本归档普遍很小，这比分两轮读、再在两轮之间核对文件版本更简单可靠。</para>
/// <para>按 Python 3.7 的候选顺序与缓存头部规则进行静态比较。
/// 无法确认兼容性的字节码不产生成功哈希；不会执行或反编译代码。</para>
/// </remarks>
public sealed class ScriptModuleScanner(IFileSystemAccess fileSystem) : IScriptModuleScanner
{
    public const string ScriptExtension = ".ts4script";

    private readonly SourceFileDiscovery _discovery = new(fileSystem);

    public async Task<ScriptModuleScanReport> ScanAsync(
        ScriptModuleScanRequest request,
        IProgress<ScriptModuleScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Sources);
        var limits = request.Limits ?? ScriptModuleLimits.Default;
        limits.Validate();

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ScriptModuleScanProgress(ScriptModuleScanPhase.Starting, "正在准备扫描…"));

        var startedAt = DateTime.UtcNow;
        var sourceIssues = new List<ScanIssue>();
        var sources = SourceFileDiscovery.NormalizeSources(request.Sources, sourceIssues);
        var validSources = _discovery.ValidateSources(sources, sourceIssues);
        var sourceSnapshots = SourceFileDiscovery.CreateSourceSnapshots(sources);

        progress?.Report(new ScriptModuleScanProgress(ScriptModuleScanPhase.Discovering, "正在查找脚本文件…"));
        var discovered = _discovery.DiscoverFiles(
            validSources,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ScriptExtension },
            sourceIssues,
            progress is null
                ? null
                : (message, count) => progress.Report(new ScriptModuleScanProgress(
                    ScriptModuleScanPhase.Discovering,
                    message,
                    DiscoveredArchiveCount: count)),
            cancellationToken);

        // 每个结果写回自己在数组里的位置，并发完成顺序不影响结果。
        var archivePaths = discovered.Keys
            .OrderBy(path => path, PathRules.Comparer)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var budget = new ResourceContentBudget(limits.MaxTotalProcessedBytes);
        var outcomes = new ArchiveOutcome[archivePaths.Length];
        var completed = 0;

        progress?.Report(new ScriptModuleScanProgress(
            ScriptModuleScanPhase.ReadingArchives,
            archivePaths.Length == 0 ? "没有需要读取的脚本文件" : "正在读取脚本文件…",
            0,
            archivePaths.Length,
            archivePaths.Length));

        await Parallel.ForEachAsync(
                Enumerable.Range(0, archivePaths.Length),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = limits.MaxDegreeOfParallelism,
                    CancellationToken = cancellationToken,
                },
                async (index, token) =>
                {
                    outcomes[index] = await ReadArchiveSafelyAsync(archivePaths[index], limits, budget, token)
                        .ConfigureAwait(false);

                    var done = Interlocked.Increment(ref completed);
                    progress?.Report(new ScriptModuleScanProgress(
                        ScriptModuleScanPhase.ReadingArchives,
                        $"正在读取脚本文件（{done}/{archivePaths.Length}）",
                        done,
                        archivePaths.Length,
                        archivePaths.Length));
                })
            .ConfigureAwait(false);

        progress?.Report(new ScriptModuleScanProgress(
            ScriptModuleScanPhase.Grouping,
            "正在比对模块名…",
            archivePaths.Length,
            archivePaths.Length,
            archivePaths.Length));

        var issues = sourceIssues.Select(FromScanIssue).ToList();
        var incomplete = new List<string>();
        var occurrences = new List<ScriptModuleOccurrence>();
        for (var index = 0; index < archivePaths.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = archivePaths[index];
            var outcome = outcomes[index];
            issues.AddRange(outcome.Issues);
            if (outcome.Modules is null)
            {
                incomplete.Add(path);
                continue;
            }

            var sourceIds = SourceIdsFor(path, validSources);
            occurrences.AddRange(outcome.Modules.Select(module => new ScriptModuleOccurrence(
                module.Entry.ModuleName,
                path,
                outcome.Stamp,
                sourceIds,
                module.Entry.EntryName,
                module.Entry.Kind,
                module.Entry.Format,
                module.Sha256,
                module.ContentLength,
                module.Issue)));
        }

        var collisions = BuildCollisions(occurrences, cancellationToken);
        var report = new ScriptModuleScanReport(
            startedAt,
            DateTime.UtcNow,
            ReadOnlyLists.Freeze(sourceSnapshots),
            limits,
            archivePaths.Length,
            archivePaths.Length - incomplete.Count,
            ReadOnlyLists.Freeze(incomplete),
            occurrences.Select(occurrence => occurrence.ModuleName).Distinct(StringComparer.Ordinal).Count(),
            collisions,
            OrderIssues(issues),
            budget.ConsumedBytes);

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ScriptModuleScanProgress(
            ScriptModuleScanPhase.Completed,
            collisions.Count == 0 ? "扫描完成，没有发现模块碰撞" : $"扫描完成，发现 {collisions.Count} 个模块碰撞",
            archivePaths.Length,
            archivePaths.Length,
            archivePaths.Length));
        return report;
    }

    private async Task<ArchiveOutcome> ReadArchiveSafelyAsync(
        string path,
        ScriptModuleLimits limits,
        ResourceContentBudget budget,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadArchiveAsync(path, limits, budget, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 一个坏文件或一处缺陷只算这一个归档失败，不中断整次扫描；内存不足照常向上抛。
            return ArchiveOutcome.Failed(Issue(
                ScriptArchiveIssueCode.UnexpectedError,
                ScriptArchiveIssueStage.Unexpected,
                path,
                "读取这个脚本文件时出现意外错误，已跳过。",
                detail: Describe(exception)));
        }
    }

    private async Task<ArchiveOutcome> ReadArchiveAsync(
        string path,
        ScriptModuleLimits limits,
        ResourceContentBudget budget,
        CancellationToken cancellationToken)
    {
        FileStamp before;
        Stream stream;
        try
        {
            before = fileSystem.GetFileStamp(path);
            if (before.Length > limits.MaxArchiveBytes)
            {
                return ArchiveOutcome.Failed(Issue(
                    ScriptArchiveIssueCode.TooLarge,
                    ScriptArchiveIssueStage.Structure,
                    path,
                    "脚本文件大得不正常，已跳过。",
                    detail: $"{before.Length} bytes > {limits.MaxArchiveBytes}"));
            }
            stream = fileSystem.OpenRead(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ArchiveOutcome.Failed(Issue(
                ScriptArchiveIssueCode.AccessFailed,
                ScriptArchiveIssueStage.Access,
                path,
                "无法打开这个脚本文件。",
                detail: Describe(exception)));
        }

        List<ModuleResult> modules;
        var issues = new List<ScriptArchiveIssue>();
        await using (stream.ConfigureAwait(false))
        {
            ScriptZipDirectory directory;
            try
            {
                directory = await ScriptZipDirectory.ReadAsync(stream, limits, cancellationToken).ConfigureAwait(false);
            }
            catch (ScriptZipException exception)
            {
                return ArchiveOutcome.Failed(Issue(exception.Code, ScriptArchiveIssueStage.Structure,
                    path, "这个脚本文件的 ZIP 结构无法分析。", detail: exception.Message));
            }

            var selected = ScriptModuleNames.GetCandidates(directory.Entries, entry => entry.Name);
            modules = new List<ModuleResult>(selected.Count);
            foreach (var candidates in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ModuleResult? result = null;
                foreach (var candidate in candidates.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var source = candidates.Entries.FirstOrDefault(other =>
                        other.Entry.Kind == candidate.Entry.Kind
                        && other.Entry.Format == ScriptModuleFormat.Source);
                    using var hasher = new ScriptModuleContentHasher(candidate.Entry.Format, source?.Value.DosTimestamp);
                    long length;
                    try
                    {
                        length = await directory.ReadEntryAsync(stream, candidate.Value, limits, budget,
                            hasher.Append, cancellationToken).ConfigureAwait(false);
                    }
                    catch (ScriptZipException exception)
                    {
                        result = ModuleResult.Failed(candidate.Entry, Issue(exception.Code,
                            exception.Code == ScriptArchiveIssueCode.BudgetExhausted
                                ? ScriptArchiveIssueStage.Budget : ScriptArchiveIssueStage.Entry,
                            path, "这个模块没有完成比较。", entryName: candidate.Entry.EntryName,
                            detail: exception.Message));
                        break;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
                    {
                        result = ModuleResult.Failed(candidate.Entry, Issue(ScriptArchiveIssueCode.EntryCorrupt,
                            ScriptArchiveIssueStage.Entry, path, "这个模块读不出来，没有比较。",
                            entryName: candidate.Entry.EntryName, detail: Describe(exception)));
                        break;
                    }

                    var content = hasher.Finish();
                    if (content.Disposition == ScriptPycDisposition.Comparable)
                    {
                        result = new ModuleResult(candidate.Entry, content.Sha256, length, null);
                        break;
                    }

                    result = ModuleResult.Failed(candidate.Entry, Issue(content.IssueCode!,
                        ScriptArchiveIssueStage.Entry, path, "无法确认这个字节码模块可用于比较。",
                        entryName: candidate.Entry.EntryName, detail: content.Detail));
                    if (content.Disposition != ScriptPycDisposition.TryNextCandidate) break;
                }

                if (result is not null)
                {
                    modules.Add(result);
                    if (result.Issue is not null) issues.Add(result.Issue);
                }
            }
        }

        FileStamp after;
        try
        {
            after = fileSystem.GetFileStamp(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ArchiveOutcome.Failed(Issue(
                ScriptArchiveIssueCode.AccessFailed,
                ScriptArchiveIssueStage.Access,
                path,
                "读取后无法再次确认这个脚本文件的状态。",
                detail: Describe(exception)));
        }

        if (after != before)
        {
            // 中途被改过，读到的条目可能来自新旧两个版本，整份作废。
            return ArchiveOutcome.Failed(Issue(
                ScriptArchiveIssueCode.ChangedDuringRead,
                ScriptArchiveIssueStage.Stability,
                path,
                "读取期间这个脚本文件被改动了，结果已作废，请重新扫描。"));
        }

        return new ArchiveOutcome(before, modules, issues);
    }

    private static IReadOnlyList<ScriptModuleCollision> BuildCollisions(
        IEnumerable<ScriptModuleOccurrence> occurrences,
        CancellationToken cancellationToken) =>
        ReadOnlyLists.Freeze(occurrences
            .GroupBy(occurrence => occurrence.ModuleName, StringComparer.Ordinal)
            .Where(group => group.Select(occurrence => occurrence.ArchivePath).Distinct(PathRules.Comparer).Skip(1).Any())
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ordered = group
                    .OrderBy(occurrence => occurrence.ArchivePath, PathRules.Comparer)
                    .ThenBy(occurrence => occurrence.ArchivePath, StringComparer.Ordinal)
                    .ToArray();
                var contentGroups = ordered
                    .Where(occurrence => occurrence.IsSuccess)
                    .GroupBy(occurrence => (occurrence.Kind, occurrence.Format, occurrence.Sha256))
                    .Select(byHash => new ScriptModuleContentGroup(byHash.Key.Sha256!, ReadOnlyLists.Freeze(byHash)))
                    .OrderByDescending(byHash => byHash.Occurrences.Count)
                    .ThenBy(byHash => byHash.Occurrences[0].ArchivePath, PathRules.Comparer)
                    .ThenBy(byHash => byHash.Sha256, StringComparer.Ordinal);
                return new ScriptModuleCollision(
                    group.Key,
                    ReadOnlyLists.Freeze(contentGroups),
                    ReadOnlyLists.Freeze(ordered.Where(occurrence => !occurrence.IsSuccess)));
            }));

    private static IReadOnlyList<string> SourceIdsFor(string path, IReadOnlyList<NormalizedSource> sources) =>
        ReadOnlyLists.Freeze(sources
            .Where(source => PathRules.IsWithin(path, source.Path))
            .OrderBy(source => source.Order)
            .ThenBy(source => source.InputIndex)
            .Select(source => source.Id));

    private static ScriptArchiveIssue FromScanIssue(ScanIssue issue) => new(
        issue.Code,
        issue.Stage switch
        {
            ScanIssueStage.SourceValidation => ScriptArchiveIssueStage.SourceValidation,
            ScanIssueStage.Discovery => ScriptArchiveIssueStage.Discovery,
            ScanIssueStage.Metadata => ScriptArchiveIssueStage.Metadata,
            _ => throw new ArgumentOutOfRangeException(nameof(issue), issue.Stage, "发现阶段不会产生这一类问题。"),
        },
        issue.Path,
        issue.Message,
        issue.SourceId);

    private static IReadOnlyList<ScriptArchiveIssue> OrderIssues(IEnumerable<ScriptArchiveIssue> issues) =>
        ReadOnlyLists.Freeze(issues
            .OrderBy(issue => issue.Stage)
            .ThenBy(issue => issue.Path, PathRules.Comparer)
            .ThenBy(issue => issue.Path, StringComparer.Ordinal)
            .ThenBy(issue => issue.EntryName, StringComparer.Ordinal)
            .ThenBy(issue => issue.Code, StringComparer.Ordinal));

    private static ScriptArchiveIssue NotZip(string path, string detail) => Issue(
        ScriptArchiveIssueCode.NotZip,
        ScriptArchiveIssueStage.Structure,
        path,
        "这个文件不是 zip，不像正常的脚本 mod。",
        detail: detail);

    private static ScriptArchiveIssue Issue(
        string code,
        ScriptArchiveIssueStage stage,
        string path,
        string message,
        string? entryName = null,
        string? detail = null) =>
        new(code, stage, path, message, EntryName: entryName, Detail: detail);

    private static string Describe(Exception exception) => $"{exception.GetType().FullName}: {exception.Message}";

    private sealed record ModuleResult(ScriptModuleEntry Entry, string? Sha256, long? ContentLength, ScriptArchiveIssue? Issue)
    {
        public static ModuleResult Failed(ScriptModuleEntry entry, ScriptArchiveIssue issue) => new(entry, null, null, issue);
    }

    private sealed record ArchiveOutcome(
        FileStamp Stamp,
        IReadOnlyList<ModuleResult>? Modules,
        IReadOnlyList<ScriptArchiveIssue> Issues)
    {
        public static ArchiveOutcome Failed(ScriptArchiveIssue issue) => new(default, null, [issue]);
    }
}
