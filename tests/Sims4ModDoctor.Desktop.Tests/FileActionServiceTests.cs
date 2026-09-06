using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModDoctor.Core.Duplicates;
using Sims4ModDoctor.Desktop.Services;

namespace Sims4ModDoctor.Desktop.Tests;

[TestClass]
public sealed class FileActionServiceTests
{
    [TestMethod]
    public void PreflightRejectsFilesChangedAfterScan()
    {
        var metadata = new FakeMetadataAccess();
        metadata.Set(@"D:\Mods\changed.package", new FileStamp(200, DateTime.UnixEpoch.AddMinutes(1)));
        var adapter = new FakeRecycleBinAdapter();
        var service = new FileActionService(adapter, metadata);

        var result = service.Preflight([
            new FileDeletionCandidate(@"D:\Mods\changed.package", 100, DateTime.UnixEpoch, "第 1 组"),
        ], []);

        Assert.IsFalse(result.CanExecute);
        Assert.AreEqual(1, result.Problems.Count);
        StringAssert.Contains(result.Problems[0].Message, "发生变化");
    }

    [TestMethod]
    public async Task DeleteAndUndoUseTheSingleRecycleBinAdapter()
    {
        const string path = @"D:\Mods\copy.package";
        var metadata = new FakeMetadataAccess();
        metadata.Set(path, new FileStamp(100, DateTime.UnixEpoch));
        var adapter = new FakeRecycleBinAdapter();
        var service = new FileActionService(adapter, metadata);
        var preflight = service.Preflight([
            new FileDeletionCandidate(path, 100, DateTime.UnixEpoch, "第 1 组"),
        ], []);

        var deletion = await service.DeleteAsync(preflight);

        Assert.AreEqual(1, deletion.CompletedPaths.Count);
        Assert.IsTrue(service.CanUndoLastDelete);
        Assert.AreEqual(1, adapter.DeleteCalls);

        var restore = await service.UndoLastDeleteAsync();

        Assert.AreEqual(1, restore.CompletedPaths.Count);
        Assert.IsFalse(service.CanUndoLastDelete);
        Assert.AreEqual(1, adapter.RestoreCalls);
    }

    [TestMethod]
    public async Task DeleteRechecksStabilityImmediatelyBeforeFileAction()
    {
        const string path = @"D:\Mods\copy.package";
        var metadata = new FakeMetadataAccess();
        metadata.Set(path, new FileStamp(100, DateTime.UnixEpoch));
        var adapter = new FakeRecycleBinAdapter();
        var service = new FileActionService(adapter, metadata);
        var preflight = service.Preflight([
            new FileDeletionCandidate(path, 100, DateTime.UnixEpoch, "第 1 组"),
        ], []);
        metadata.Set(path, new FileStamp(101, DateTime.UnixEpoch));

        var result = await service.DeleteAsync(preflight);

        Assert.AreEqual(0, result.CompletedPaths.Count);
        Assert.AreEqual(1, result.Failures.Count);
        Assert.AreEqual(0, adapter.DeleteCalls);
    }

    [TestMethod]
    public async Task ClearingLastDeleteRemovesUndoState()
    {
        const string path = @"D:\Mods\copy.package";
        var metadata = new FakeMetadataAccess();
        metadata.Set(path, new FileStamp(100, DateTime.UnixEpoch));
        var service = new FileActionService(new FakeRecycleBinAdapter(), metadata);
        var preflight = service.Preflight([
            new FileDeletionCandidate(path, 100, DateTime.UnixEpoch, "第 1 组"),
        ], []);
        await service.DeleteAsync(preflight);
        Assert.IsTrue(service.CanUndoLastDelete);

        service.ClearLastDelete();

        Assert.IsFalse(service.CanUndoLastDelete);
    }

    private sealed class FakeMetadataAccess : IFileMetadataAccess
    {
        private readonly Dictionary<string, FileStamp> stamps = new(StringComparer.OrdinalIgnoreCase);

        public bool FileExists(string path) => stamps.ContainsKey(path);

        public FileStamp GetFileStamp(string path) => stamps[path];

        public void Set(string path, FileStamp stamp) => stamps[path] = stamp;
    }

    private sealed class FakeRecycleBinAdapter : IRecycleBinAdapter
    {
        public int DeleteCalls { get; private set; }

        public int RestoreCalls { get; private set; }

        public bool CanRecycle(string path) => true;

        public Task<RecycleBinDeleteResult> MoveToRecycleBinAsync(
            IReadOnlyList<string> paths,
            CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            return Task.FromResult(new RecycleBinDeleteResult(
                paths.Select(path => new RecycleBinItem(path, [1, 2, 0, 0])).ToArray(),
                []));
        }

        public Task<FileActionBatchResult> RestoreAsync(
            IReadOnlyList<RecycleBinItem> items,
            CancellationToken cancellationToken = default)
        {
            RestoreCalls++;
            return Task.FromResult(new FileActionBatchResult(
                items.Select(item => item.OriginalPath).ToArray(),
                [],
                false));
        }
    }
}
