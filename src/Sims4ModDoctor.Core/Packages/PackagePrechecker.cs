using System.Buffers.Binary;
using Sims4ModDoctor.Core.Duplicates;

namespace Sims4ModDoctor.Core.Packages;

/// <summary>
/// 预检通过时的结果：header 里已验证过的字段，以及读取开始前的文件戳。
/// 文件戳留给调用方在读取结束后复核，用来发现「读到一半文件被改了」。
/// </summary>
public sealed record PackagePrecheckResult(
    string FullPath,
    DbpfPrecheck.DbpfHeader Header,
    FileStamp StampBefore);

/// <summary>
/// 把 <see cref="DbpfPrecheck"/> 的纯判断接到真实文件上。
/// 只读取 header 与索引头，总共不超过 100 字节。
/// </summary>
public sealed class PackagePrechecker(IFileSystemAccess fileSystem)
{
    public static PackagePrechecker CreateDefault() => new(new PhysicalFileSystemAccess());

    /// <summary>
    /// 返回值二选一：<paramref name="result"/> 有值表示可以继续交给读取器，
    /// 否则返回结构化问题。可预期的失败不抛异常。
    /// </summary>
    public PackageReadIssue? Inspect(
        string packagePath,
        PackageSafetyLimits limits,
        out PackagePrecheckResult? result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentNullException.ThrowIfNull(limits);
        result = null;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(packagePath);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new PackageReadIssue(
                PackageReadIssueCode.PathInvalid,
                PackageReadStage.Access,
                packagePath,
                "这个路径无法识别。",
                exception.Message);
        }

        FileStamp stampBefore;
        try
        {
            stampBefore = fileSystem.GetFileStamp(fullPath);
        }
        catch (FileNotFoundException)
        {
            return new PackageReadIssue(
                PackageReadIssueCode.NotFound,
                PackageReadStage.Access,
                fullPath,
                "找不到这个文件，它可能已经被移动或删除。");
        }
        catch (DirectoryNotFoundException)
        {
            return new PackageReadIssue(
                PackageReadIssueCode.NotFound,
                PackageReadStage.Access,
                fullPath,
                "找不到这个文件所在的目录。");
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            return new PackageReadIssue(
                PackageReadIssueCode.AccessFailed,
                PackageReadStage.Access,
                fullPath,
                "无法读取这个文件。",
                exception.Message);
        }

        if (stampBefore.Length < DbpfPrecheck.HeaderLength)
        {
            return DbpfPrecheck.TooSmall(fullPath, stampBefore.Length);
        }

        if (stampBefore.Length > limits.MaxFileSizeBytes)
        {
            return new PackageReadIssue(
                PackageReadIssueCode.TooLarge,
                PackageReadStage.Precheck,
                fullPath,
                "这个 package 超出了本工具支持的体积上限。",
                $"文件长度 {stampBefore.Length} 字节，上限 {limits.MaxFileSizeBytes} 字节。");
        }

        try
        {
            using var stream = fileSystem.OpenRead(fullPath);

            Span<byte> header = stackalloc byte[DbpfPrecheck.HeaderLength];
            stream.ReadExactly(header);

            var headerIssue = DbpfPrecheck.ValidateHeader(
                header,
                stampBefore.Length,
                fullPath,
                limits,
                out var parsed);

            if (headerIssue is not null)
            {
                return headerIssue;
            }

            if (parsed.EntryCount > 0)
            {
                var shapeIssue = InspectIndexShape(stream, parsed, fullPath);
                if (shapeIssue is not null)
                {
                    return shapeIssue;
                }
            }

            result = new PackagePrecheckResult(fullPath, parsed, stampBefore);
            return null;
        }
        catch (EndOfStreamException)
        {
            // header 或索引头声称存在，实际读不满：文件在预检期间被截短，或长度本身就不可信。
            return new PackageReadIssue(
                PackageReadIssueCode.IndexOutOfBounds,
                PackageReadStage.Precheck,
                fullPath,
                "这个 package 的内容比它声明的短，文件可能已损坏或被截断。");
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new PackageReadIssue(
                PackageReadIssueCode.AccessFailed,
                PackageReadStage.Access,
                fullPath,
                "无法读取这个文件。",
                exception.Message);
        }
    }

    private static PackageReadIssue? InspectIndexShape(
        Stream stream,
        DbpfPrecheck.DbpfHeader parsed,
        string fullPath)
    {
        stream.Seek(parsed.IndexPosition, SeekOrigin.Begin);

        Span<byte> indexType = stackalloc byte[DbpfPrecheck.IndexTypeFieldLength];
        stream.ReadExactly(indexType);

        return DbpfPrecheck.ValidateIndexShape(
            BinaryPrimitives.ReadUInt32LittleEndian(indexType),
            parsed.EntryCount,
            parsed.IndexSize,
            fullPath);
    }
}
