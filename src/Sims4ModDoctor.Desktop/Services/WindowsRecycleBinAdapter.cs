using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;

namespace Sims4ModDoctor.Desktop.Services;

public sealed class WindowsRecycleBinAdapter : IRecycleBinAdapter
{
    private const uint FofSilent = 0x0004;
    private const uint FofNoConfirmation = 0x0010;
    private const uint FofAllowUndo = 0x0040;
    private const uint FofNoErrorUi = 0x0400;
    private const uint FofxRecycleOnDelete = 0x00080000;

    public bool CanRecycle(string path)
    {
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrWhiteSpace(root)
                && new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public Task<RecycleBinDeleteResult> MoveToRecycleBinAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default) =>
        RunStaAsync(() => MoveToRecycleBin(paths, cancellationToken), cancellationToken);

    public Task<FileActionBatchResult> RestoreAsync(
        IReadOnlyList<RecycleBinItem> items,
        CancellationToken cancellationToken = default) =>
        RunStaAsync(() => Restore(items, cancellationToken), cancellationToken);

    private static RecycleBinDeleteResult MoveToRecycleBin(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var normalizedPaths = paths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var failures = new List<FileActionFailure>();
        var queuedItems = new List<IShellItem>();
        IFileOperation? operation = null;
        uint cookie = 0;
        var sink = new FileOperationProgressSink();

        try
        {
            operation = CreateFileOperation();
            ThrowIfFailed(operation.SetOperationFlags(
                FofSilent | FofNoConfirmation | FofAllowUndo | FofNoErrorUi | FofxRecycleOnDelete));
            ThrowIfFailed(operation.Advise(sink, out cookie));

            foreach (var path in normalizedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var item = CreateShellItem(path);
                    queuedItems.Add(item);
                    ThrowIfFailed(operation.DeleteItem(item, null));
                }
                catch (Exception exception) when (exception is COMException or IOException or UnauthorizedAccessException)
                {
                    failures.Add(new FileActionFailure(path, exception.Message));
                }
            }

            if (queuedItems.Count > 0)
            {
                ThrowIfFailed(operation.PerformOperations());
                ThrowIfFailed(operation.GetAnyOperationsAborted(out var aborted));
                if (aborted)
                {
                    failures.Add(new FileActionFailure(string.Empty, "Windows 取消了部分回收站操作"));
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is COMException or IOException or UnauthorizedAccessException)
        {
            failures.Add(new FileActionFailure(string.Empty, exception.Message));
        }
        finally
        {
            if (operation is not null && cookie != 0)
            {
                operation.Unadvise(cookie);
            }

            foreach (var item in queuedItems)
            {
                ReleaseComObject(item);
            }

            ReleaseComObject(operation);
        }

        var completed = sink.DeletedItems
            .GroupBy(item => item.OriginalPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToArray();
        var completedPaths = completed
            .Select(item => item.OriginalPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var path in normalizedPaths)
        {
            if (completedPaths.Contains(path)
                || failures.Any(failure => StringComparer.OrdinalIgnoreCase.Equals(failure.Path, path)))
            {
                continue;
            }

            failures.Add(new FileActionFailure(
                path,
                File.Exists(path)
                    ? "文件未能移入 Windows 回收站"
                    : "文件已移走，但没有取得可撤回的回收站记录"));
        }

        return new RecycleBinDeleteResult(completed, failures);
    }

    private static FileActionBatchResult Restore(
        IReadOnlyList<RecycleBinItem> items,
        CancellationToken cancellationToken)
    {
        var completed = new List<string>();
        var failures = new List<FileActionFailure>();

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(item.OriginalPath) || Directory.Exists(item.OriginalPath))
            {
                failures.Add(new FileActionFailure(item.OriginalPath, "原位置已有同名文件，未覆盖"));
                continue;
            }

            var parentPath = Path.GetDirectoryName(item.OriginalPath);
            if (string.IsNullOrWhiteSpace(parentPath) || !Directory.Exists(parentPath))
            {
                failures.Add(new FileActionFailure(item.OriginalPath, "原文件夹已不存在"));
                continue;
            }

            IFileOperation? operation = null;
            IShellItem? recycledItem = null;
            IShellItem? destinationFolder = null;
            try
            {
                recycledItem = CreateShellItem(item.AbsolutePidl);
                destinationFolder = CreateShellItem(parentPath);
                operation = CreateFileOperation();
                ThrowIfFailed(operation.SetOperationFlags(FofSilent | FofNoConfirmation | FofNoErrorUi));
                ThrowIfFailed(operation.MoveItem(
                    recycledItem,
                    destinationFolder,
                    Path.GetFileName(item.OriginalPath),
                    null));
                ThrowIfFailed(operation.PerformOperations());
                ThrowIfFailed(operation.GetAnyOperationsAborted(out var aborted));

                if (!aborted && File.Exists(item.OriginalPath))
                {
                    completed.Add(item.OriginalPath);
                }
                else
                {
                    failures.Add(new FileActionFailure(item.OriginalPath, "Windows 未能从回收站恢复该文件"));
                }
            }
            catch (Exception exception) when (exception is COMException or IOException or UnauthorizedAccessException)
            {
                failures.Add(new FileActionFailure(item.OriginalPath, exception.Message));
            }
            finally
            {
                ReleaseComObject(recycledItem);
                ReleaseComObject(destinationFolder);
                ReleaseComObject(operation);
            }
        }

        return new FileActionBatchResult(completed, failures, items.Count > completed.Count);
    }

    private static Task<T> RunStaAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.SetResult(action());
            }
            catch (OperationCanceledException exception)
            {
                completion.SetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Sims4ModDoctor.WindowsRecycleBin",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static IFileOperation CreateFileOperation()
    {
        var type = Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), throwOnError: true)!;
        return (IFileOperation)Activator.CreateInstance(type)!;
    }

    private static IShellItem CreateShellItem(string path)
    {
        var shellItemId = typeof(IShellItem).GUID;
        ThrowIfFailed(SHCreateItemFromParsingName(path, IntPtr.Zero, ref shellItemId, out var item));
        return item;
    }

    private static IShellItem CreateShellItem(byte[] absolutePidl)
    {
        var memory = Marshal.AllocCoTaskMem(absolutePidl.Length);
        try
        {
            Marshal.Copy(absolutePidl, 0, memory, absolutePidl.Length);
            var shellItemId = typeof(IShellItem).GUID;
            ThrowIfFailed(SHCreateItemFromIDList(memory, ref shellItemId, out var item));
            return item;
        }
        finally
        {
            Marshal.FreeCoTaskMem(memory);
        }
    }

    private static void ThrowIfFailed(int result)
    {
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    [ComVisible(true)]
    private sealed class FileOperationProgressSink : IFileOperationProgressSink
    {
        private readonly ConcurrentBag<RecycleBinItem> deletedItems = [];

        public IReadOnlyList<RecycleBinItem> DeletedItems => deletedItems.ToArray();

        public int StartOperations() => 0;

        public int FinishOperations(int result) => 0;

        public int PreRenameItem(uint flags, IShellItem item, string newName) => 0;

        public int PostRenameItem(uint flags, IShellItem item, string newName, int result, IShellItem? newItem) => 0;

        public int PreMoveItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName) => 0;

        public int PostMoveItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName, int result, IShellItem? newItem) => 0;

        public int PreCopyItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName) => 0;

        public int PostCopyItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName, int result, IShellItem? newItem) => 0;

        public int PreDeleteItem(uint flags, IShellItem item) => 0;

        public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? newItem)
        {
            if (result >= 0 && newItem is not null)
            {
                try
                {
                    deletedItems.Add(new RecycleBinItem(GetFileSystemPath(item), GetAbsolutePidl(newItem)));
                }
                catch (Exception)
                {
                    // The caller verifies every queued path after the Shell operation.
                }
            }

            return 0;
        }

        public int PreNewItem(uint flags, IShellItem destinationFolder, string newName) => 0;

        public int PostNewItem(uint flags, IShellItem destinationFolder, string newName, string? templateName, uint attributes, int result, IShellItem? newItem) => 0;

        public int UpdateProgress(uint workTotal, uint workSoFar) => 0;

        public int ResetTimer() => 0;

        public int PauseTimer() => 0;

        public int ResumeTimer() => 0;

        private static string GetFileSystemPath(IShellItem item)
        {
            const uint sigdnFileSystemPath = 0x80058000;
            ThrowIfFailed(item.GetDisplayName(sigdnFileSystemPath, out var value));
            try
            {
                return Marshal.PtrToStringUni(value) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeCoTaskMem(value);
            }
        }

        private static byte[] GetAbsolutePidl(IShellItem item)
        {
            ThrowIfFailed(SHGetIDListFromObject(item, out var pidl));
            try
            {
                var length = checked((int)ILGetSize(pidl));
                var bytes = new byte[length];
                Marshal.Copy(pidl, bytes, 0, length);
                return bytes;
            }
            finally
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path,
        IntPtr bindingContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem shellItem);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHCreateItemFromIDList(
        IntPtr pidl,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem shellItem);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHGetIDListFromObject(
        [MarshalAs(UnmanagedType.IUnknown)] object source,
        out IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern uint ILGetSize(IntPtr pidl);

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(IntPtr bindingContext, ref Guid handlerId, ref Guid interfaceId, out IntPtr result);

        [PreserveSig]
        int GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItem parent);

        [PreserveSig]
        int GetDisplayName(uint displayNameType, out IntPtr name);

        [PreserveSig]
        int GetAttributes(uint requestedAttributes, out uint attributes);

        [PreserveSig]
        int Compare([MarshalAs(UnmanagedType.Interface)] IShellItem other, uint hint, out int order);
    }

    [ComImport]
    [Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise([MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint operationFlags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog([MarshalAs(UnmanagedType.Interface)] object progressDialog);
        [PreserveSig] int SetProperties([MarshalAs(UnmanagedType.Interface)] object properties);
        [PreserveSig] int SetOwnerWindow(IntPtr ownerWindow);
        [PreserveSig] int ApplyPropertiesToItem([MarshalAs(UnmanagedType.Interface)] IShellItem item);
        [PreserveSig] int ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        [PreserveSig] int RenameItem([MarshalAs(UnmanagedType.Interface)] IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink? sink);
        [PreserveSig] int RenameItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        [PreserveSig] int MoveItem([MarshalAs(UnmanagedType.Interface)] IShellItem item, [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName, [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink? sink);
        [PreserveSig] int MoveItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder);
        [PreserveSig] int CopyItem([MarshalAs(UnmanagedType.Interface)] IShellItem item, [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? copyName, [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink? sink);
        [PreserveSig] int CopyItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder);
        [PreserveSig] int DeleteItem([MarshalAs(UnmanagedType.Interface)] IShellItem item, [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink? sink);
        [PreserveSig] int DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        [PreserveSig] int NewItem([MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder, uint fileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? templateName, [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink? sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, [MarshalAs(UnmanagedType.Interface)] IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        [PreserveSig] int PostRenameItem(uint flags, [MarshalAs(UnmanagedType.Interface)] IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, int result, [MarshalAs(UnmanagedType.Interface)] IShellItem? newItem);
        [PreserveSig] int PreMoveItem(uint flags, [MarshalAs(UnmanagedType.Interface)] IShellItem item, [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
        [PreserveSig] int PostMoveItem(uint flags, [MarshalAs(UnmanagedType.Interface)] IShellItem item, [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int result, [MarshalAs(UnmanagedType.Interface)] IShellItem? newItem);
        [PreserveSig] int PreCopyItem(uint flags, [MarshalAs(UnmanagedType.Interface)] IShellItem item, [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
        [PreserveSig] int PostCopyItem(uint flags, [MarshalAs(UnmanagedType.Interface)] IShellItem item, [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int result, [MarshalAs(UnmanagedType.Interface)] IShellItem? newItem);
        [PreserveSig] int PreDeleteItem(uint flags, [MarshalAs(UnmanagedType.Interface)] IShellItem item);
        [PreserveSig] int PostDeleteItem(uint flags, [MarshalAs(UnmanagedType.Interface)] IShellItem item, int result, [MarshalAs(UnmanagedType.Interface)] IShellItem? newItem);
        [PreserveSig] int PreNewItem(uint flags, [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        [PreserveSig] int PostNewItem(uint flags, [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string newName, [MarshalAs(UnmanagedType.LPWStr)] string? templateName, uint fileAttributes, int result, [MarshalAs(UnmanagedType.Interface)] IShellItem? newItem);
        [PreserveSig] int UpdateProgress(uint workTotal, uint workSoFar);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }
}
