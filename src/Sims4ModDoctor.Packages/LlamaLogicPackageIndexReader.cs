using LlamaLogic.Packages;
using Sims4ModDoctor.Core.Duplicates;
using Sims4ModDoctor.Core.Packages;
using CoreResourceKey = Sims4ModDoctor.Core.Packages.ResourceKey;
using LlamaResourceKey = LlamaLogic.Packages.ResourceKey;

namespace Sims4ModDoctor.Packages;

/// <summary>
/// <see cref="IPackageIndexReader"/> 的唯一实现，也是整个解决方案里唯一引用
/// <c>LlamaLogic.Packages</c> 的地方。
/// </summary>
/// <remarks>
/// 边界：
/// <list type="bullet">
/// <item>只使用只读入口；不调用任何写入、合并或保存 API。</item>
/// <item>把文件交给第三方库之前，先跑本项目自己的
/// <see cref="PackagePrechecker"/>，使伪造的资源数量无法触发库内的大额分配。</item>
/// <item>不向外暴露任何第三方类型。</item>
/// </list>
/// </remarks>
public sealed class LlamaLogicPackageIndexReader(
    PackagePrechecker prechecker,
    IFileSystemAccess fileSystem,
    PackageSafetyLimits? limits = null) : IPackageIndexReader
{
    private readonly PackageSafetyLimits _limits = limits ?? PackageSafetyLimits.Default;

    public static LlamaLogicPackageIndexReader CreateDefault()
    {
        var fileSystem = new PhysicalFileSystemAccess();
        return new LlamaLogicPackageIndexReader(new PackagePrechecker(fileSystem), fileSystem);
    }

    public async Task<PackageReadResult> ReadIndexAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var precheckIssue = prechecker.Inspect(packagePath, _limits, out var precheck);
        if (precheckIssue is not null)
        {
            return PackageReadResult.Failure(precheckIssue);
        }

        // 预检返回 null 问题时必定带回结果，这里只是让可空性显式收敛。
        ArgumentNullException.ThrowIfNull(precheck);
        var fullPath = precheck.FullPath;

        try
        {
            await using var package = await DataBasePackedFile
                .FromPathAsync(fullPath, forReadOnly: true)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            var version = package.FileVersion;
            var keys = await package
                .GetKeysAsync(ResourceKeyOrder.Preserve, cancellationToken)
                .ConfigureAwait(false);

            var resources = new List<PackageResourceEntry>(keys.Count);
            for (var ordinal = 0; ordinal < keys.Count; ordinal++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var key = keys[ordinal];
                var (compression, compressionRaw) = ReadCompression(package, key);

                resources.Add(new PackageResourceEntry(
                    ToCoreKey(key),
                    ordinal,
                    ReadContentSize(package, key),
                    compression,
                    compressionRaw));
            }

            // 读取期间文件被改写时，索引与内容可能已经对不上，
            // 此时不能返回一份看起来完整的结果。形状与 StableFileHasher 相同。
            var stampAfter = fileSystem.GetFileStamp(fullPath);
            if (stampAfter != precheck.StampBefore)
            {
                return PackageReadResult.Failure(new PackageReadIssue(
                    PackageReadIssueCode.ChangedDuringRead,
                    PackageReadStage.Stability,
                    fullPath,
                    "这个文件在读取过程中被改动了，本次结果已作废。",
                    $"读取前 {precheck.StampBefore.Length} 字节 / "
                        + $"{precheck.StampBefore.LastWriteTimeUtc:O}，"
                        + $"读取后 {stampAfter.Length} 字节 / {stampAfter.LastWriteTimeUtc:O}。"));
            }

            return PackageReadResult.Success(new PackageIndexSummary(
                fullPath,
                version.Major,
                version.Minor,
                precheck.StampBefore.Length,
                precheck.StampBefore,
                resources));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 第三方库对损坏输入抛什么异常没有契约，所以这里放宽到通用异常，
            // 让一个坏文件只坏掉它自己那条结果，不中断整批分析。
            // OutOfMemoryException 例外：进程状态已不可信，继续跑下去没有意义。
            return PackageReadResult.Failure(new PackageReadIssue(
                PackageReadIssueCode.IndexReadFailed,
                PackageReadStage.Index,
                fullPath,
                "这个 package 的索引读不出来，文件可能已损坏。",
                $"{exception.GetType().Name}: {exception.Message}"));
        }
    }

    /// <summary>
    /// 取资源内容大小。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这一项不是纯索引数据：库为了回答尺寸，需要按资源类型触碰内容，
    /// 对结构合法但内容不完整的文件会直接抛异常。本轮只读索引，
    /// 因此这里把它降级为尽力而为——取不到就记 <see langword="null"/>，
    /// 不让一条元数据毁掉整个文件的 TGI 索引。
    /// </remarks>
    private static long? ReadContentSize(DataBasePackedFile package, LlamaResourceKey key)
    {
        try
        {
            return package.GetSize(key);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// 取压缩模式。与尺寸同理，取不到时归入
    /// <see cref="PackageCompression.Unknown"/> 而不是让整份索引失败。
    /// </summary>
    private static (PackageCompression Compression, string Raw) ReadCompression(
        DataBasePackedFile package,
        LlamaResourceKey key)
    {
        try
        {
            var mode = package.GetExplicitCompressionMode(key);
            return (MapCompression(mode), mode.ToString());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return (PackageCompression.Unknown, "unavailable");
        }
    }

    private static CoreResourceKey ToCoreKey(LlamaResourceKey key) =>
        new((uint)key.Type, key.Group, key.FullInstance);

    /// <summary>
    /// 把第三方枚举规范化成本项目的压缩分类。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这张映射表依据的是库的枚举命名，尚未与 s4pe 对同一批资源逐项对拍。
    /// 拿不准的一律归入 <see cref="PackageCompression.Unknown"/>，而不是猜一个近似值；
    /// 原始取值由 <see cref="PackageResourceEntry.CompressionRaw"/> 保留。
    /// </remarks>
    private static PackageCompression MapCompression(CompressionMode mode) => mode switch
    {
        CompressionMode.ForceOff => PackageCompression.None,
        CompressionMode.ForceZLib or CompressionMode.CallerSuppliedZLib => PackageCompression.Zlib,
        CompressionMode.ForceInternal or CompressionMode.CallerSuppliedInternal =>
            PackageCompression.RefPack,
        CompressionMode.SetDeletedFlag => PackageCompression.Deleted,
        _ => PackageCompression.Unknown,
    };
}
