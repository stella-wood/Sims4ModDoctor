using Sims4ModDoctor.Core.Duplicates;

namespace Sims4ModDoctor.Core.Tests;

internal sealed class CountingFileSystemAccess(IFileSystemAccess inner) : IFileSystemAccess
{
    private readonly Dictionary<string, int> _openCounts = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> OpenCounts => _openCounts;

    public bool DirectoryExists(string path) => inner.DirectoryExists(path);

    public IReadOnlyList<string> EnumerateFileSystemEntries(string directory) =>
        inner.EnumerateFileSystemEntries(directory);

    public FileAttributes GetAttributes(string path) => inner.GetAttributes(path);

    public FileStamp GetFileStamp(string path) => inner.GetFileStamp(path);

    public Stream OpenRead(string path)
    {
        _openCounts[path] = _openCounts.GetValueOrDefault(path) + 1;
        return inner.OpenRead(path);
    }
}

internal sealed class FaultingFileSystemAccess(
    IFileSystemAccess inner,
    string blockedDirectory) : IFileSystemAccess
{
    public bool DirectoryExists(string path) => inner.DirectoryExists(path);

    public IReadOnlyList<string> EnumerateFileSystemEntries(string directory)
    {
        if (string.Equals(directory, blockedDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Access denied for test.");
        }

        return inner.EnumerateFileSystemEntries(directory);
    }

    public FileAttributes GetAttributes(string path) => inner.GetAttributes(path);

    public FileStamp GetFileStamp(string path) => inner.GetFileStamp(path);

    public Stream OpenRead(string path) => inner.OpenRead(path);
}

internal sealed class ReparsePointFileSystemAccess(
    IFileSystemAccess inner,
    string reparsePoint) : IFileSystemAccess
{
    public bool DirectoryExists(string path) => inner.DirectoryExists(path);

    public IReadOnlyList<string> EnumerateFileSystemEntries(string directory) =>
        inner.EnumerateFileSystemEntries(directory);

    public FileAttributes GetAttributes(string path)
    {
        var attributes = inner.GetAttributes(path);
        return string.Equals(path, reparsePoint, StringComparison.OrdinalIgnoreCase)
            ? attributes | FileAttributes.ReparsePoint
            : attributes;
    }

    public FileStamp GetFileStamp(string path) => inner.GetFileStamp(path);

    public Stream OpenRead(string path) => inner.OpenRead(path);
}

internal sealed class SequencedStampFileSystem(
    byte[] content,
    params FileStamp[] stamps) : IFileSystemAccess
{
    private int _stampIndex;

    public int OpenCount { get; private set; }

    public bool DirectoryExists(string path) => true;

    public IReadOnlyList<string> EnumerateFileSystemEntries(string directory) => [];

    public FileAttributes GetAttributes(string path) => FileAttributes.Normal;

    public FileStamp GetFileStamp(string path)
    {
        var index = Math.Min(_stampIndex, stamps.Length - 1);
        _stampIndex++;
        return stamps[index];
    }

    public Stream OpenRead(string path)
    {
        OpenCount++;
        return new MemoryStream(content, writable: false);
    }
}
