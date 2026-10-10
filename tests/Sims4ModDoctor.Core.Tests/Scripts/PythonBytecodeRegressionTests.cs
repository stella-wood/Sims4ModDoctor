using System.Buffers.Binary;
using System.IO.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModDoctor.Core.Duplicates;
using Sims4ModDoctor.Core.Scripts;

namespace Sims4ModDoctor.Core.Tests.Scripts;

[TestClass]
public sealed class PythonBytecodeRegressionTests
{
    private static byte[] Pyc(byte[] body, uint flags = 0, uint metadata = 0)
    {
        var header = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x0A0D0D42);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), metadata);
        return [.. header, .. body];
    }

    private static void Write(TempDirectory temp, string path, params (string Name, byte[] Content)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2020, 1, 2, 3, 4, 6, TimeSpan.Zero);
                using var stream = entry.Open();
                stream.Write(content);
            }
        }
        temp.WriteBytes(path, memory.ToArray());
    }

    private static Task<ScriptModuleScanReport> Scan(TempDirectory temp) =>
        new ScriptModuleScanner(new PhysicalFileSystemAccess()).ScanAsync(
            new ScriptModuleScanRequest([new ScanSource("mods", temp.Path)]));

    [TestMethod]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public async Task UnsupportedFlagsUseAvailableSourceInsteadOfABytecodeHash(int flags)
    {
        using var temp = new TempDirectory();
        var invalid = Pyc("same bytecode"u8.ToArray(), (uint)flags);
        Write(temp, "a.ts4script", ("m.pyc", invalid), ("m.py", "x=1"u8.ToArray()));
        Write(temp, "b.ts4script", ("m.pyc", invalid), ("m.py", "x=2"u8.ToArray()));

        var report = await Scan(temp);

        var collision = report.Collisions.Single();
        Assert.AreEqual(ScriptModuleVerdict.Different, collision.Verdict);
        Assert.IsTrue(collision.Occurrences.All(occurrence => occurrence.Format == ScriptModuleFormat.Source));
        Assert.AreEqual(0, report.Issues.Count);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(3)]
    [DataRow(15)]
    [DataRow(16)]
    public async Task TruncatedHeaderOrEmptyBytecodeBodyDoesNotProduceASuccessfulHash(int length)
    {
        using var temp = new TempDirectory();
        var invalid = Pyc([])[..length];
        Write(temp, "a.ts4script", ("m.pyc", invalid));
        Write(temp, "b.ts4script", ("m.pyc", invalid));

        var report = await Scan(temp);

        var collision = report.Collisions.Single();
        Assert.AreEqual(ScriptModuleVerdict.Incomplete, collision.Verdict);
        Assert.AreEqual(0, collision.ContentGroups.Count);
        Assert.AreEqual(2, collision.Uncompared.Count);
        Assert.IsTrue(collision.Uncompared.All(occurrence => occurrence.Issue?.Code == ScriptArchiveIssueCode.EntryCorrupt));
    }

    [TestMethod]
    public async Task StaleTimestampBytecodeUsesDifferentSourceFallbacks()
    {
        using var temp = new TempDirectory();
        var stale = Pyc("same cached body"u8.ToArray(), metadata: 1);
        Write(temp, "a.ts4script", ("m.pyc", stale), ("m.py", "x=1"u8.ToArray()));
        Write(temp, "b.ts4script", ("m.pyc", stale), ("m.py", "x=2"u8.ToArray()));

        var report = await Scan(temp);

        var collision = report.Collisions.Single();
        Assert.AreEqual(ScriptModuleVerdict.Different, collision.Verdict);
        Assert.IsTrue(collision.Occurrences.All(occurrence => occurrence.EntryName == "m.py"));
        Assert.AreEqual(0, report.Issues.Count);
    }

    [TestMethod]
    public async Task TimestampAndAcceptedHashHeadersWithTheSameBodyCompareIdentically()
    {
        using var temp = new TempDirectory();
        var body = "same body"u8.ToArray();
        Write(temp, "a.ts4script", ("m.pyc", Pyc(body, flags: 0, metadata: 1)));
        Write(temp, "b.ts4script", ("m.pyc", Pyc(body, flags: 1, metadata: 999)));

        var report = await Scan(temp);

        Assert.AreEqual(ScriptModuleVerdict.Identical, report.Collisions.Single().Verdict);
        Assert.AreEqual(0, report.Issues.Count);
    }

    [TestMethod]
    public async Task SourceBytesEqualToTheBytecodeBodyAreDifferentFormats()
    {
        using var temp = new TempDirectory();
        var body = "same bytes"u8.ToArray();
        Write(temp, "a.ts4script", ("m.pyc", Pyc(body)));
        Write(temp, "b.ts4script", ("m.py", body));

        var report = await Scan(temp);

        Assert.AreEqual(ScriptModuleVerdict.Different, report.Collisions.Single().Verdict);
        Assert.AreEqual(0, report.Issues.Count);
    }

    [TestMethod]
    public async Task AModuleAndPackageWithTheSameNameAndBytesAreDifferentKinds()
    {
        using var temp = new TempDirectory();
        var source = "x=1"u8.ToArray();
        Write(temp, "a.ts4script", ("lib.py", source));
        Write(temp, "b.ts4script", ("lib/__init__.py", source));

        var report = await Scan(temp);

        var collision = report.Collisions.Single();
        Assert.AreEqual("lib", collision.ModuleName);
        Assert.AreEqual(ScriptModuleVerdict.Different, collision.Verdict);
        Assert.AreEqual(0, report.Issues.Count);
    }

    [TestMethod]
    public async Task AnUnsupportedMagicWithoutSourceLeavesTheModuleUncompared()
    {
        using var temp = new TempDirectory();
        var invalid = Pyc("same body"u8.ToArray());
        invalid[0] = 0x61;
        Write(temp, "a.ts4script", ("m.pyc", invalid));
        Write(temp, "b.ts4script", ("m.pyc", invalid));

        var report = await Scan(temp);

        var collision = report.Collisions.Single();
        Assert.AreEqual(ScriptModuleVerdict.Incomplete, collision.Verdict);
        Assert.AreEqual(0, collision.ContentGroups.Count);
        Assert.AreEqual(2, collision.Uncompared.Count);
        Assert.IsTrue(collision.Uncompared.All(occurrence => occurrence.Issue is not null && occurrence.Sha256 is null));
    }
}
