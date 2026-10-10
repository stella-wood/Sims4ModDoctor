using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModDoctor.Core.Packages;

namespace Sims4ModDoctor.Packages.Tests;

/// <summary>
/// 针对本机真实 Mod 语料的验证。默认跳过，不随仓库分发任何真实 Mod。
/// </summary>
/// <remarks>
/// 自造 fixture 能证明解析规则被正确实现，但证明不了这些规则覆盖了真实世界的形态。
/// 这一组回答的是另一个问题：预检会不会把玩家正常的 Mod 拦下来。
/// <para>
/// 运行方式：
/// <code>
/// SIMS4_MOD_DOCTOR_REAL_MODS=/path/to/Mods dotnet test tests/Sims4ModDoctor.Packages.Tests
/// </code>
/// </para>
/// </remarks>
[TestClass]
public sealed class RealCorpusTests
{
    private const string DirectoryVariable = "SIMS4_MOD_DOCTOR_REAL_MODS";

    private static string RequireCorpus()
    {
        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            Assert.Inconclusive(
                $"Set {DirectoryVariable} to a directory of real .package files to run this test.");
        }

        return directory;
    }

    [TestMethod]
    [TestCategory("RealCorpus")]
    public async Task AcceptsEveryPackageInTheLocalCorpus()
    {
        var directory = RequireCorpus();
        var paths = Directory.GetFiles(directory, "*.package", SearchOption.AllDirectories);
        Assert.IsGreaterThan(0, paths.Length, "语料目录里没有 .package 文件。");

        var reader = LlamaLogicPackageIndexReader.CreateDefault();
        var rejected = new List<string>();
        var totalResources = 0;

        foreach (var path in paths)
        {
            var result = await reader.ReadIndexAsync(path);
            if (result.IsSuccess)
            {
                totalResources += result.Summary!.ResourceCount;
                continue;
            }

            // 只记文件名，不记完整路径：失败清单可能被贴进 issue 或日志。
            rejected.Add($"{Path.GetFileName(path)} → {result.Issue!.Code}：{result.Issue.Detail}");
        }

        Assert.AreEqual(
            0,
            rejected.Count,
            $"{rejected.Count}/{paths.Length} 个真实 package 被拒绝："
                + Environment.NewLine
                + string.Join(Environment.NewLine, rejected.Take(20)));
        Assert.IsGreaterThan(0, totalResources);
    }

    /// <summary>
    /// 真实语料里必须出现至少一个使用了公共常量字段的 package，
    /// 否则这份语料本身就测不出按固定长度步进读索引的错误。
    /// </summary>
    [TestMethod]
    [TestCategory("RealCorpus")]
    public async Task CorpusContainsPackagesThatUseConstantIndexFields()
    {
        var directory = RequireCorpus();
        var paths = Directory.GetFiles(directory, "*.package", SearchOption.AllDirectories);

        var withConstants = 0;
        foreach (var path in paths)
        {
            await using var stream = File.OpenRead(path);
            var header = new byte[DbpfPrecheck.HeaderLength];
            if (await stream.ReadAsync(header) < DbpfPrecheck.HeaderLength)
            {
                continue;
            }

            var count = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                header.AsSpan(0x24));
            var position = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                header.AsSpan(0x40));
            if (count == 0)
            {
                continue;
            }

            stream.Seek(position, SeekOrigin.Begin);
            var indexType = new byte[DbpfPrecheck.IndexTypeFieldLength];
            if (await stream.ReadAsync(indexType) < indexType.Length)
            {
                continue;
            }

            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(indexType) != 0)
            {
                withConstants++;
            }
        }

        Assert.IsGreaterThan(
            0,
            withConstants,
            "这份语料里没有任何使用公共常量字段的 package，覆盖不到索引记录长度可变的情形。");
    }
}
