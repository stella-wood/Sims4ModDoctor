namespace Sims4ModDoctor.Core.Duplicates;

public sealed record ScanSource(
    string Id,
    string Path,
    bool Enabled = true,
    int Order = 0,
    string? Label = null);

public sealed record DuplicateScanRequest(
    IReadOnlyList<ScanSource> Sources,
    string? ModsRoot = null,
    IReadOnlyCollection<string>? IncludedExtensions = null);

public enum DuplicateScanPhase
{
    Starting,
    Discovering,
    Hashing,
    Completed,
}

public sealed record DuplicateScanProgress(
    DuplicateScanPhase Phase,
    string Message,
    int CompletedItems = 0,
    int? TotalItems = null,
    int DiscoveredFileCount = 0);

public sealed record ScanSourceSnapshot(
    string Id,
    string Path,
    int Order,
    string Label,
    bool IsFocusRange,
    bool IsReferenceRange);

public readonly record struct FileStamp(long Length, DateTime LastWriteTimeUtc);

public sealed record DuplicateFile(
    string Path,
    long Size,
    DateTime LastWriteTimeUtc,
    IReadOnlyList<string> SourceIds,
    IReadOnlyList<string> PrimarySourceIds,
    bool IsInsideMods,
    int DirectoryDepth);

public enum DuplicateSection
{
    FocusCrossScope,
    FocusInternal,
    ReferenceInternal,
    Ordinary,
}

public sealed record DuplicateGroup(
    string Sha256,
    long FileSize,
    DuplicateSection Section,
    IReadOnlyList<DuplicateFile> Files,
    string SuggestedKeepPath,
    IReadOnlyList<string> SuggestedDeletePaths)
{
    public long ReclaimableBytes => checked(FileSize * (Files.Count - 1L));
}

public enum ScanIssueStage
{
    SourceValidation,
    Discovery,
    Metadata,
    Hashing,
}

public sealed record ScanIssue(
    string Code,
    ScanIssueStage Stage,
    string Path,
    string Message,
    string? SourceId = null);

public sealed record DuplicateReport(
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc,
    IReadOnlyList<ScanSourceSnapshot> Sources,
    int DiscoveredFileCount,
    int HashedFileCount,
    IReadOnlyList<DuplicateGroup> Groups,
    IReadOnlyList<ScanIssue> Issues)
{
    public int DuplicateFileCount => Groups.Sum(group => group.Files.Count);

    public long ReclaimableBytes => Groups.Sum(group => group.ReclaimableBytes);
}
