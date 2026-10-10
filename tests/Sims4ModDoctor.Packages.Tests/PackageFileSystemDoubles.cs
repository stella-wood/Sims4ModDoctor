using Sims4ModDoctor.Core.Duplicates;

namespace Sims4ModDoctor.Packages.Tests;

/// <summary>
/// 在第 <see cref="_triggerOnCall"/> 次取文件戳时改掉返回值，
/// 用来模拟「读取过程中文件被改写」，而不必真的在测试里竞态写文件。
/// </summary>
internal sealed class StampDriftFileSystem(
    IFileSystemAccess inner,
    int triggerOnCall) : IFileSystemAccess
{
    private int _calls;

    public bool DirectoryExists(string path) => inner.DirectoryExists(path);

    public IReadOnlyList<string> EnumerateFileSystemEntries(string directory) =>
        inner.EnumerateFileSystemEntries(directory);

    public FileAttributes GetAttributes(string path) => inner.GetAttributes(path);

    public FileStamp GetFileStamp(string path)
    {
        var stamp = inner.GetFileStamp(path);
        _calls++;
        return _calls >= triggerOnCall
            ? stamp with { LastWriteTimeUtc = stamp.LastWriteTimeUtc.AddMinutes(1) }
            : stamp;
    }

    public Stream OpenRead(string path) => inner.OpenRead(path);
}

/// <summary>
/// 预检读完文件之后触发取消，用来验证取消发生在「预检已过、尚未交给第三方库」
/// 这个窗口时，异常仍然原样抛出，而不是被转成一个失败结果。
/// </summary>
internal sealed class CancelOnOpenFileSystem(
    IFileSystemAccess inner,
    CancellationTokenSource source) : IFileSystemAccess
{
    public bool DirectoryExists(string path) => inner.DirectoryExists(path);

    public IReadOnlyList<string> EnumerateFileSystemEntries(string directory) =>
        inner.EnumerateFileSystemEntries(directory);

    public FileAttributes GetAttributes(string path) => inner.GetAttributes(path);

    public FileStamp GetFileStamp(string path) => inner.GetFileStamp(path);

    public Stream OpenRead(string path)
    {
        var stream = inner.OpenRead(path);
        source.Cancel();
        return stream;
    }
}
