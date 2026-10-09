namespace Sims4ModDoctor.Core.Duplicates;

public sealed class DuplicateScanner(
    IFileSystemAccess fileSystem,
    IStableFileHasher fileHasher,
    DuplicateSelectionService selectionService)
{
    private readonly SourceFileDiscovery _discovery = new(fileSystem);

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
        var sources = SourceFileDiscovery.NormalizeSources(request.Sources, issues);
        var validSources = _discovery.ValidateSources(sources, issues);
        var sourceSnapshots = SourceFileDiscovery.CreateSourceSnapshots(sources);
        var extensions = SourceFileDiscovery.NormalizeExtensions(request.IncludedExtensions);
        var candidates = _discovery.DiscoverFiles(
            validSources,
            extensions,
            issues,
            progress is null
                ? null
                : (message, discovered) => progress.Report(new DuplicateScanProgress(
                    DuplicateScanPhase.Discovering,
                    message,
                    DiscoveredFileCount: discovered)),
            cancellationToken);
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
            SourceFileDiscovery.OrderIssues(issues));

        progress?.Report(new DuplicateScanProgress(
            DuplicateScanPhase.Completed,
            groups.Count == 0 ? "扫描完成，没有发现重复文件" : $"扫描完成，发现 {groups.Count} 组重复文件",
            hashCandidates.Length,
            hashCandidates.Length,
            candidates.Count));

        return report;
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

    private sealed record HashedCandidate(string Path, StableFileHash Hash);
}
