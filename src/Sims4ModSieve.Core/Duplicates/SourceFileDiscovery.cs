namespace Sims4ModSieve.Core.Duplicates;

/// <summary>
/// 扫描来源的规范化、校验与文件发现。重复文件扫描与候选冲突扫描共用这一份，
/// 使「同一个物理文件只处理一次」「不跟随 reparse point」「单个目录失败不影响其他来源」
/// 这些规则在两处保持一致。
/// </summary>
internal sealed class SourceFileDiscovery(IFileSystemAccess fileSystem)
{
    public static IReadOnlyList<NormalizedSource> NormalizeSources(
        IReadOnlyList<ScanSource> requestedSources,
        List<ScanIssue> issues)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<NormalizedSource>();

        for (var inputIndex = 0; inputIndex < requestedSources.Count; inputIndex++)
        {
            var source = requestedSources[inputIndex];
            if (!source.Enabled)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(source.Id) || !seenIds.Add(source.Id))
            {
                issues.Add(new ScanIssue(
                    "invalid-source-id",
                    ScanIssueStage.SourceValidation,
                    source.Path,
                    "Enabled scan sources must have unique, non-empty identifiers.",
                    source.Id));
                continue;
            }

            try
            {
                var path = PathRules.Normalize(source.Path);
                normalized.Add(new NormalizedSource(
                    source.Id,
                    path,
                    source.Order,
                    string.IsNullOrWhiteSpace(source.Label) ? path : source.Label.Trim(),
                    PathRules.Depth(path),
                    inputIndex));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                issues.Add(new ScanIssue(
                    "invalid-source-path",
                    ScanIssueStage.SourceValidation,
                    source.Path,
                    exception.Message,
                    source.Id));
            }
        }

        return normalized
            .OrderBy(source => source.Order)
            .ThenBy(source => source.InputIndex)
            .ToArray();
    }

    public IReadOnlyList<NormalizedSource> ValidateSources(
        IReadOnlyList<NormalizedSource> sources,
        List<ScanIssue> issues)
    {
        var valid = new List<NormalizedSource>();
        foreach (var source in sources)
        {
            if (!fileSystem.DirectoryExists(source.Path))
            {
                issues.Add(new ScanIssue(
                    "source-not-found",
                    ScanIssueStage.SourceValidation,
                    source.Path,
                    "Scan source does not exist or is not a directory.",
                    source.Id));
                continue;
            }

            try
            {
                if ((fileSystem.GetAttributes(source.Path) & FileAttributes.ReparsePoint) != 0)
                {
                    issues.Add(new ScanIssue(
                        "source-is-reparse-point",
                        ScanIssueStage.SourceValidation,
                        source.Path,
                        "Reparse-point scan sources are not followed.",
                        source.Id));
                    continue;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                issues.Add(new ScanIssue(
                    "source-metadata-failed",
                    ScanIssueStage.SourceValidation,
                    source.Path,
                    exception.Message,
                    source.Id));
                continue;
            }

            valid.Add(source);
        }

        return valid;
    }

    /// <param name="reportProgress">
    /// 发现阶段的进度回调：（给玩家看的消息，目前已发现的文件数）。
    /// 两种扫描各自把它包装成自己的进度模型。
    /// </param>
    public Dictionary<string, DiscoveredFile> DiscoverFiles(
        IReadOnlyList<NormalizedSource> sources,
        HashSet<string>? extensions,
        List<ScanIssue> issues,
        Action<string, int>? reportProgress,
        CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, DiscoveredFile>(PathRules.Comparer);
        var explicitRoots = sources.Select(source => source.Path).ToHashSet(PathRules.Comparer);
        var roots = sources
            .GroupBy(source => source.Path, PathRules.Comparer)
            .Select(group => group.First())
            .OrderByDescending(source => source.Depth)
            .ThenBy(source => source.Order)
            .ThenBy(source => source.InputIndex);

        foreach (var source in roots)
        {
            reportProgress?.Invoke($"正在查看 {source.Label}…", files.Count);

            var pending = new Stack<string>();
            pending.Push(source.Path);

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                IReadOnlyList<string> entries;

                try
                {
                    entries = fileSystem.EnumerateFileSystemEntries(directory);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    issues.Add(new ScanIssue(
                        "directory-enumeration-failed",
                        ScanIssueStage.Discovery,
                        directory,
                        exception.Message,
                        source.Id));
                    continue;
                }

                foreach (var entry in entries.OrderBy(path => path, PathRules.Comparer).ThenBy(path => path, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string path;
                    FileAttributes attributes;

                    try
                    {
                        path = PathRules.Normalize(entry);
                        attributes = fileSystem.GetAttributes(path);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        issues.Add(new ScanIssue(
                            "entry-metadata-failed",
                            ScanIssueStage.Metadata,
                            entry,
                            exception.Message,
                            source.Id));
                        continue;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        issues.Add(new ScanIssue(
                            "reparse-point-skipped",
                            ScanIssueStage.Discovery,
                            path,
                            "Reparse points are not followed.",
                            source.Id));
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (!PathRules.Comparer.Equals(path, source.Path) && explicitRoots.Contains(path))
                        {
                            continue;
                        }

                        pending.Push(path);
                        continue;
                    }

                    if (extensions is not null && !extensions.Contains(Path.GetExtension(path)))
                    {
                        continue;
                    }

                    if (files.ContainsKey(path))
                    {
                        continue;
                    }

                    try
                    {
                        files.Add(path, new DiscoveredFile(path, fileSystem.GetFileStamp(path)));
                        if (files.Count % 100 == 0)
                        {
                            reportProgress?.Invoke($"已找到 {files.Count:N0} 个文件…", files.Count);
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        issues.Add(new ScanIssue(
                            "file-metadata-failed",
                            ScanIssueStage.Metadata,
                            path,
                            exception.Message,
                            source.Id));
                    }
                }
            }
        }

        return files;
    }

    public static IReadOnlyList<ScanSourceSnapshot> CreateSourceSnapshots(IReadOnlyList<NormalizedSource> sources)
    {
        return sources.Select(source => new ScanSourceSnapshot(
                source.Id,
                source.Path,
                source.Order,
                source.Label,
                sources.Any(other => PathRules.IsStrictDescendant(source.Path, other.Path)),
                sources.Any(other => PathRules.IsStrictDescendant(other.Path, source.Path))))
            .ToArray();
    }

    public static HashSet<string>? NormalizeExtensions(IReadOnlyCollection<string>? extensions)
    {
        if (extensions is null || extensions.Count == 0)
        {
            return null;
        }

        return extensions
            .Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Select(extension => extension.Trim())
            .Select(extension => extension.StartsWith('.') ? extension : $".{extension}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<ScanIssue> OrderIssues(IEnumerable<ScanIssue> issues) => issues
        .OrderBy(issue => issue.Stage)
        .ThenBy(issue => issue.Path, PathRules.Comparer)
        .ThenBy(issue => issue.Code, StringComparer.Ordinal)
        .ToArray();
}

internal sealed record NormalizedSource(
    string Id,
    string Path,
    int Order,
    string Label,
    int Depth,
    int InputIndex);

internal sealed record DiscoveredFile(string Path, FileStamp DiscoveredStamp);
