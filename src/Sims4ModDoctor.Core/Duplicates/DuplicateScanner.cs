namespace Sims4ModDoctor.Core.Duplicates;

public sealed class DuplicateScanner(
    IFileSystemAccess fileSystem,
    IStableFileHasher fileHasher,
    DuplicateSelectionService selectionService)
{
    public static DuplicateScanner CreateDefault()
    {
        var fileSystem = new PhysicalFileSystemAccess();
        return new DuplicateScanner(
            fileSystem,
            new StableFileHasher(fileSystem),
            new DuplicateSelectionService());
    }

    public async Task<DuplicateReport> ScanAsync(
        DuplicateScanRequest request,
        CancellationToken cancellationToken = default)
    {
        return await ScanAsync(request, progress: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DuplicateReport> ScanAsync(
        DuplicateScanRequest request,
        IProgress<DuplicateScanProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Sources);

        progress?.Report(new DuplicateScanProgress(
            DuplicateScanPhase.Starting,
            "正在准备扫描…"));

        var startedAt = DateTime.UtcNow;
        var issues = new List<ScanIssue>();
        var sources = NormalizeSources(request.Sources, issues);
        var validSources = ValidateSources(sources, issues);
        var sourceSnapshots = CreateSourceSnapshots(sources);
        var extensions = NormalizeExtensions(request.IncludedExtensions);
        var candidates = DiscoverFiles(validSources, extensions, issues, progress, cancellationToken);
        var modsRoot = NormalizeOptionalPath(request.ModsRoot, issues);

        var hashedFiles = 0;
        var hashedCandidates = new List<HashedCandidate>();
        var hashCandidates = candidates.Values
            .GroupBy(candidate => candidate.DiscoveredStamp.Length)
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key)
            .SelectMany(group => group.OrderBy(item => item.Path, PathRules.Comparer))
            .ToArray();

        progress?.Report(new DuplicateScanProgress(
            DuplicateScanPhase.Hashing,
            hashCandidates.Length == 0 ? "没有需要进一步比对的文件" : "正在核对文件内容…",
            TotalItems: hashCandidates.Length,
            DiscoveredFileCount: candidates.Count));

        foreach (var candidate in hashCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var hash = await fileHasher.HashAsync(candidate.Path, cancellationToken)
                    .ConfigureAwait(false);
                hashedFiles++;
                hashedCandidates.Add(new HashedCandidate(candidate.Path, hash));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (FileChangedDuringHashException exception)
            {
                issues.Add(new ScanIssue(
                    "file-changed-during-hash",
                    ScanIssueStage.Hashing,
                    candidate.Path,
                    exception.Message));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                issues.Add(new ScanIssue(
                    "hash-failed",
                    ScanIssueStage.Hashing,
                    candidate.Path,
                    exception.Message));
            }

            progress?.Report(new DuplicateScanProgress(
                DuplicateScanPhase.Hashing,
                $"正在核对文件内容（{hashedFiles + issues.Count(issue => issue.Stage == ScanIssueStage.Hashing)}/{hashCandidates.Length}）",
                hashedFiles + issues.Count(issue => issue.Stage == ScanIssueStage.Hashing),
                hashCandidates.Length,
                candidates.Count));
        }

        var groups = BuildGroups(hashedCandidates, validSources, modsRoot);
        var report = new DuplicateReport(
            startedAt,
            DateTime.UtcNow,
            sourceSnapshots,
            candidates.Count,
            hashedFiles,
            groups,
            OrderIssues(issues));

        progress?.Report(new DuplicateScanProgress(
            DuplicateScanPhase.Completed,
            groups.Count == 0 ? "扫描完成，没有发现重复文件" : $"扫描完成，发现 {groups.Count} 组重复文件",
            hashCandidates.Length,
            hashCandidates.Length,
            candidates.Count));

        return report;
    }

    private static IReadOnlyList<NormalizedSource> NormalizeSources(
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

    private IReadOnlyList<NormalizedSource> ValidateSources(
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

    private Dictionary<string, DiscoveredCandidate> DiscoverFiles(
        IReadOnlyList<NormalizedSource> sources,
        HashSet<string>? extensions,
        List<ScanIssue> issues,
        IProgress<DuplicateScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, DiscoveredCandidate>(PathRules.Comparer);
        var explicitRoots = sources.Select(source => source.Path).ToHashSet(PathRules.Comparer);
        var roots = sources
            .GroupBy(source => source.Path, PathRules.Comparer)
            .Select(group => group.First())
            .OrderByDescending(source => source.Depth)
            .ThenBy(source => source.Order)
            .ThenBy(source => source.InputIndex);

        foreach (var source in roots)
        {
            progress?.Report(new DuplicateScanProgress(
                DuplicateScanPhase.Discovering,
                $"正在查看 {source.Label}…",
                DiscoveredFileCount: files.Count));

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
                        files.Add(path, new DiscoveredCandidate(path, fileSystem.GetFileStamp(path)));
                        if (files.Count % 100 == 0)
                        {
                            progress?.Report(new DuplicateScanProgress(
                                DuplicateScanPhase.Discovering,
                                $"已找到 {files.Count:N0} 个文件…",
                                DiscoveredFileCount: files.Count));
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

    private IReadOnlyList<DuplicateGroup> BuildGroups(
        IReadOnlyList<HashedCandidate> hashedCandidates,
        IReadOnlyList<NormalizedSource> sources,
        string? modsRoot)
    {
        var focusIds = sources
            .Where(source => sources.Any(other => PathRules.IsStrictDescendant(source.Path, other.Path)))
            .Select(source => source.Id)
            .ToHashSet(StringComparer.Ordinal);
        var referenceIds = sources
            .Where(source => sources.Any(other => PathRules.IsStrictDescendant(other.Path, source.Path)))
            .Select(source => source.Id)
            .ToHashSet(StringComparer.Ordinal);

        var groups = new List<DuplicateGroup>();
        foreach (var hashGroup in hashedCandidates
                     .GroupBy(candidate => (candidate.Hash.Stamp.Length, candidate.Hash.Sha256))
                     .Where(group => group.Count() > 1))
        {
            var files = hashGroup
                .Select(candidate => CreateDuplicateFile(candidate, sources, modsRoot))
                .OrderBy(file => file.Path, PathRules.Comparer)
                .ThenBy(file => file.Path, StringComparer.Ordinal)
                .ToArray();
            var selection = selectionService.Select(files);
            var section = Classify(files, focusIds, referenceIds);

            groups.Add(new DuplicateGroup(
                hashGroup.Key.Sha256,
                hashGroup.Key.Length,
                section,
                files,
                selection.KeepPath,
                selection.DeletePaths));
        }

        return groups
            .OrderBy(group => group.Section)
            .ThenByDescending(group => group.ReclaimableBytes)
            .ThenBy(group => group.Sha256, StringComparer.Ordinal)
            .ToArray();
    }

    private static DuplicateFile CreateDuplicateFile(
        HashedCandidate candidate,
        IReadOnlyList<NormalizedSource> sources,
        string? modsRoot)
    {
        var matching = sources.Where(source => PathRules.IsWithin(candidate.Path, source.Path)).ToArray();
        var deepest = matching.Length == 0 ? -1 : matching.Max(source => source.Depth);
        var sourceIds = matching
            .OrderBy(source => source.Order)
            .ThenBy(source => source.InputIndex)
            .Select(source => source.Id)
            .ToArray();
        var primaryIds = matching
            .Where(source => source.Depth == deepest)
            .OrderBy(source => source.Order)
            .ThenBy(source => source.InputIndex)
            .Select(source => source.Id)
            .ToArray();

        return new DuplicateFile(
            candidate.Path,
            candidate.Hash.Stamp.Length,
            candidate.Hash.Stamp.LastWriteTimeUtc,
            sourceIds,
            primaryIds,
            modsRoot is not null && PathRules.IsWithin(candidate.Path, modsRoot),
            PathRules.Depth(Path.GetDirectoryName(candidate.Path) ?? candidate.Path));
    }

    private static DuplicateSection Classify(
        IReadOnlyList<DuplicateFile> files,
        IReadOnlySet<string> focusIds,
        IReadOnlySet<string> referenceIds)
    {
        var hasFocus = files.Any(file => file.PrimarySourceIds.Any(focusIds.Contains));
        var hasNonFocus = files.Any(file => file.PrimarySourceIds.All(id => !focusIds.Contains(id)));

        if (hasFocus && hasNonFocus)
        {
            return DuplicateSection.FocusCrossScope;
        }

        if (hasFocus)
        {
            return DuplicateSection.FocusInternal;
        }

        return files.All(file => file.PrimarySourceIds.Any(referenceIds.Contains))
            ? DuplicateSection.ReferenceInternal
            : DuplicateSection.Ordinary;
    }

    private static IReadOnlyList<ScanSourceSnapshot> CreateSourceSnapshots(IReadOnlyList<NormalizedSource> sources)
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

    private static HashSet<string>? NormalizeExtensions(IReadOnlyCollection<string>? extensions)
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

    private static string? NormalizeOptionalPath(string? path, List<ScanIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return PathRules.Normalize(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            issues.Add(new ScanIssue(
                "invalid-mods-root",
                ScanIssueStage.SourceValidation,
                path,
                exception.Message));
            return null;
        }
    }

    private static IReadOnlyList<ScanIssue> OrderIssues(IEnumerable<ScanIssue> issues) => issues
        .OrderBy(issue => issue.Stage)
        .ThenBy(issue => issue.Path, PathRules.Comparer)
        .ThenBy(issue => issue.Code, StringComparer.Ordinal)
        .ToArray();

    private sealed record NormalizedSource(
        string Id,
        string Path,
        int Order,
        string Label,
        int Depth,
        int InputIndex);

    private sealed record DiscoveredCandidate(string Path, FileStamp DiscoveredStamp);

    private sealed record HashedCandidate(string Path, StableFileHash Hash);
}
