using System.IO;
using Sims4ModDoctor.Core.Duplicates;

namespace Sims4ModDoctor.Desktop.Services;

public sealed record FileDeletionCandidate(
    string Path,
    long ExpectedSize,
    DateTime ExpectedLastWriteTimeUtc,
    string GroupLabel);

public sealed record FileActionFailure(string Path, string Message);

public sealed record FileDeletionPreflight(
    IReadOnlyList<FileDeletionCandidate> Candidates,
    IReadOnlyList<FileActionFailure> Problems,
    IReadOnlyList<string> FullySelectedGroups)
{
    public bool CanExecute => Candidates.Count > 0 && Problems.Count == 0;

    public int FileCount => Candidates.Count;

    public long TotalBytes => Candidates.Sum(candidate => candidate.ExpectedSize);
}

public sealed record FileActionBatchResult(
    IReadOnlyList<string> CompletedPaths,
    IReadOnlyList<FileActionFailure> Failures,
    bool CanUndo);

public sealed record RecycleBinItem(string OriginalPath, byte[] AbsolutePidl);

public sealed record RecycleBinDeleteResult(
    IReadOnlyList<RecycleBinItem> CompletedItems,
    IReadOnlyList<FileActionFailure> Failures);

public interface IFileMetadataAccess
{
    bool FileExists(string path);

    FileStamp GetFileStamp(string path);
}

public sealed class PhysicalFileMetadataAccess : IFileMetadataAccess
{
    public bool FileExists(string path) => File.Exists(path);

    public FileStamp GetFileStamp(string path)
    {
        var info = new FileInfo(path);
        return new FileStamp(info.Length, info.LastWriteTimeUtc);
    }
}

public interface IRecycleBinAdapter
{
    bool CanRecycle(string path);

    Task<RecycleBinDeleteResult> MoveToRecycleBinAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default);

    Task<FileActionBatchResult> RestoreAsync(
        IReadOnlyList<RecycleBinItem> items,
        CancellationToken cancellationToken = default);
}

public interface IFileActionService
{
    bool CanUndoLastDelete { get; }

    void ClearLastDelete();

    FileDeletionPreflight Preflight(
        IReadOnlyList<FileDeletionCandidate> candidates,
        IReadOnlyList<string> fullySelectedGroups);

    Task<FileActionBatchResult> DeleteAsync(
        FileDeletionPreflight preflight,
        CancellationToken cancellationToken = default);

    Task<FileActionBatchResult> UndoLastDeleteAsync(CancellationToken cancellationToken = default);
}

public sealed class FileActionService(
    IRecycleBinAdapter recycleBin,
    IFileMetadataAccess metadata) : IFileActionService
{
    private IReadOnlyList<RecycleBinItem> lastDeletedItems = Array.Empty<RecycleBinItem>();

    public FileActionService()
        : this(new WindowsRecycleBinAdapter(), new PhysicalFileMetadataAccess())
    {
    }

    public bool CanUndoLastDelete => lastDeletedItems.Count > 0;

    public void ClearLastDelete()
    {
        lastDeletedItems = Array.Empty<RecycleBinItem>();
    }

    public FileDeletionPreflight Preflight(
        IReadOnlyList<FileDeletionCandidate> candidates,
        IReadOnlyList<string> fullySelectedGroups)
    {
        var uniqueCandidates = candidates
            .GroupBy(candidate => Path.GetFullPath(candidate.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First() with { Path = group.Key })
            .ToArray();
        var problems = new List<FileActionFailure>();

        foreach (var candidate in uniqueCandidates)
        {
            if (!metadata.FileExists(candidate.Path))
            {
                problems.Add(new FileActionFailure(candidate.Path, "文件已不存在，请重新扫描"));
                continue;
            }

            FileStamp currentStamp;
            try
            {
                currentStamp = metadata.GetFileStamp(candidate.Path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                problems.Add(new FileActionFailure(candidate.Path, exception.Message));
                continue;
            }

            if (currentStamp.Length != candidate.ExpectedSize
                || currentStamp.LastWriteTimeUtc != candidate.ExpectedLastWriteTimeUtc)
            {
                problems.Add(new FileActionFailure(candidate.Path, "文件在扫描后发生变化，请重新扫描"));
                continue;
            }

            if (!recycleBin.CanRecycle(candidate.Path))
            {
                problems.Add(new FileActionFailure(candidate.Path, "该位置不支持可靠的 Windows 回收站操作"));
            }
        }

        return new FileDeletionPreflight(
            uniqueCandidates,
            problems,
            fullySelectedGroups.Distinct(StringComparer.Ordinal).ToArray());
    }

    public async Task<FileActionBatchResult> DeleteAsync(
        FileDeletionPreflight preflight,
        CancellationToken cancellationToken = default)
    {
        var refreshed = Preflight(preflight.Candidates, preflight.FullySelectedGroups);
        if (!refreshed.CanExecute)
        {
            return new FileActionBatchResult([], refreshed.Problems, CanUndoLastDelete);
        }

        var result = await recycleBin
            .MoveToRecycleBinAsync(
                refreshed.Candidates.Select(candidate => candidate.Path).ToArray(),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.CompletedItems.Count > 0)
        {
            lastDeletedItems = result.CompletedItems;
        }
        return new FileActionBatchResult(
            result.CompletedItems.Select(item => item.OriginalPath).ToArray(),
            result.Failures,
            CanUndoLastDelete);
    }

    public async Task<FileActionBatchResult> UndoLastDeleteAsync(
        CancellationToken cancellationToken = default)
    {
        if (lastDeletedItems.Count == 0)
        {
            return new FileActionBatchResult([], [], false);
        }

        var result = await recycleBin
            .RestoreAsync(lastDeletedItems, cancellationToken)
            .ConfigureAwait(false);
        var completed = result.CompletedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        lastDeletedItems = lastDeletedItems
            .Where(item => !completed.Contains(item.OriginalPath))
            .ToArray();

        return result with { CanUndo = CanUndoLastDelete };
    }
}
