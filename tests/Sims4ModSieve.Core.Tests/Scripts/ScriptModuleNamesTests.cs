using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Scripts;

namespace Sims4ModSieve.Core.Tests.Scripts;

/// <summary>
/// 模块名换算规则，经由扫描器的公开结果验证（换算类本身是 internal）。
/// </summary>
[TestClass]
public sealed class ScriptModuleNamesTests
{
    private static async Task<IReadOnlyList<string>> CollidingNamesAsync(params string[] entryNames)
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        foreach (var archive in new[] { "a", "b" })
        {
            using var memory = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(
                memory, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var name in entryNames)
                {
                    using var stream = zip.CreateEntry(name).Open();
                    // Valid supported bytecode keeps these tests focused on path parsing.
                    byte[] content = name.EndsWith(".pyc", StringComparison.Ordinal)
                        ? [0x42, 0x0D, 0x0D, 0x0A, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, (byte)'x']
                        : [(byte)'x'];
                    stream.Write(content);
                }
            }
            temp.WriteBytes($"Mods/{archive}.ts4script", memory.ToArray());
        }

        var report = await new ScriptModuleScanner(new PhysicalFileSystemAccess())
            .ScanAsync(new ScriptModuleScanRequest([new ScanSource("mods", mods)]));
        return report.Collisions.Select(collision => collision.ModuleName).ToArray();
    }

    [TestMethod]
    public async Task PathsBecomeDottedModuleNames()
    {
        CollectionAssert.AreEqual(
            new[] { "pkg", "pkg.sub.mod", "top" },
            (await CollidingNamesAsync("top.pyc", "pkg/sub/mod.pyc", "pkg/__init__.py")).ToArray());
    }

    [TestMethod]
    public async Task BackslashSeparatorsAreNormalized()
    {
        CollectionAssert.AreEqual(
            new[] { "pkg.mod" },
            (await CollidingNamesAsync("pkg\\mod.pyc")).ToArray());
    }

    [TestMethod]
    public async Task NamesPythonCannotImportAreSkipped()
    {
        CollectionAssert.AreEqual(
            Array.Empty<string>(),
            (await CollidingNamesAsync(
                "__init__.pyc",
                "has.dot.pyc",
                "UPPER.PYC",
                "a/__pycache__/b.cpython-37.pyc",
                "notes.txt")).ToArray());
    }
}
