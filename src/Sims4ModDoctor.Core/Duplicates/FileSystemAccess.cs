namespace Sims4ModDoctor.Core.Duplicates;

public interface IFileSystemAccess
{
    bool DirectoryExists(string path);

    IReadOnlyList<string> EnumerateFileSystemEntries(string directory);

    FileAttributes GetAttributes(string path);

    FileStamp GetFileStamp(string path);

    Stream OpenRead(string path);
}

public sealed class PhysicalFileSystemAccess : IFileSystemAccess
{
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IReadOnlyList<string> EnumerateFileSystemEntries(string directory) =>
        Directory.EnumerateFileSystemEntries(directory).ToArray();

    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);

    public FileStamp GetFileStamp(string path)
    {
        var info = new FileInfo(path);
        return new FileStamp(info.Length, info.LastWriteTimeUtc);
    }

    public Stream OpenRead(string path) => new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        bufferSize: 128 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
}
