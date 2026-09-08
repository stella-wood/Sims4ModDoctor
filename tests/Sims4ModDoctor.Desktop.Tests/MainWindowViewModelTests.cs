using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModDoctor.Core.Duplicates;
using Sims4ModDoctor.Desktop.Infrastructure;
using Sims4ModDoctor.Desktop.Services;
using Sims4ModDoctor.Desktop.ViewModels;

namespace Sims4ModDoctor.Desktop.Tests;

[TestClass]
public sealed class MainWindowViewModelTests
{
    [TestMethod]
    public void SavedUnavailablePathsAreRetainedWithoutChangingTheirEnabledState()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"Sims4ModDoctor-Missing-{Guid.NewGuid():N}");
        var settings = new FakeSettingsStore(new DuplicateSettings(
            missing,
            [new SavedScanSource(missing, true)]));

        var viewModel = CreateViewModel(settings);

        Assert.AreEqual(missing, viewModel.ModsRoot);
        Assert.AreEqual(1, viewModel.Sources.Count);
        Assert.IsTrue(viewModel.Sources[0].IsEnabled);
        Assert.IsFalse(viewModel.Sources[0].IsAvailable);
        Assert.IsTrue(viewModel.Sources[0].HasAvailabilityWarning);
        Assert.AreEqual("不可访问", viewModel.Sources[0].AvailabilityText);
        Assert.IsTrue(viewModel.HasModsRootAvailabilityWarning);
        Assert.AreEqual(0, settings.SaveCalls);
    }

    [TestMethod]
    public void AddingAFolderNamedModsDoesNotSetModsRoot()
    {
        using var temporary = new TemporaryDirectory();
        var existing = temporary.CreateDirectory("existing");
        var namedMods = temporary.CreateDirectory("Mods");
        var settings = new FakeSettingsStore(new DuplicateSettings(
            null,
            [new SavedScanSource(existing, true)]));
        var picker = new FakeFolderPicker(namedMods);
        var viewModel = CreateViewModel(settings, picker);

        viewModel.AddSourceCommand.Execute(null);

        Assert.IsNull(viewModel.ModsRoot);
        Assert.IsTrue(viewModel.Sources.Any(source => PathsEqual(source.Path, namedMods)));
    }

    [TestMethod]
    public void ChoosingModsRootAddsItAsAnEnabledSourceAndSavesImmediately()
    {
        using var temporary = new TemporaryDirectory();
        var existing = temporary.CreateDirectory("existing");
        var mods = temporary.CreateDirectory("My Mods");
        var settings = new FakeSettingsStore(new DuplicateSettings(
            null,
            [new SavedScanSource(existing, true)]));
        var viewModel = CreateViewModel(settings, new FakeFolderPicker(mods));

        viewModel.ChangeModsRootCommand.Execute(null);

        Assert.AreEqual(mods, viewModel.ModsRoot);
        var source = viewModel.Sources.Single(item => PathsEqual(item.Path, mods));
        Assert.IsTrue(source.IsEnabled);
        Assert.IsTrue(source.IsAvailable);
        Assert.AreEqual("更改", viewModel.ModsRootActionText);
        Assert.IsNotNull(settings.LastSaved);
        Assert.AreEqual(mods, settings.LastSaved.ModsRoot);
        Assert.IsTrue(settings.LastSaved.Sources.Any(item => PathsEqual(item.Path, mods) && item.Enabled));
    }

    [TestMethod]
    public void ChangingModsRootRetainsTheOldRootAsAnOrdinarySource()
    {
        using var temporary = new TemporaryDirectory();
        var existing = temporary.CreateDirectory("existing");
        var first = temporary.CreateDirectory("first");
        var second = temporary.CreateDirectory("second");
        var settings = new FakeSettingsStore(new DuplicateSettings(
            null,
            [new SavedScanSource(existing, true)]));
        var viewModel = CreateViewModel(settings, new FakeFolderPicker(first, second));

        viewModel.ChangeModsRootCommand.Execute(null);
        viewModel.ChangeModsRootCommand.Execute(null);

        Assert.AreEqual(second, viewModel.ModsRoot);
        Assert.IsTrue(viewModel.Sources.Any(item => PathsEqual(item.Path, first)));
        Assert.IsTrue(viewModel.Sources.Any(item => PathsEqual(item.Path, second)));
    }

    [TestMethod]
    public async Task ReconnectedSourceRefreshesBeforeScanAndProducesResults()
    {
        using var temporary = new TemporaryDirectory();
        var recovered = Path.Combine(temporary.Path, "recovered");
        var settings = new FakeSettingsStore(new DuplicateSettings(
            null,
            [new SavedScanSource(recovered, true)]));
        var viewModel = CreateViewModel(settings);
        Assert.IsFalse(viewModel.Sources.Single().IsAvailable);

        Directory.CreateDirectory(recovered);
        await File.WriteAllTextAsync(Path.Combine(recovered, "one.package"), "same");
        await File.WriteAllTextAsync(Path.Combine(recovered, "two.package"), "same");

        await viewModel.StartScanCommand.ExecuteAsync();

        Assert.IsTrue(viewModel.Sources.Single().IsAvailable);
        Assert.AreEqual(1, viewModel.DuplicateGroupCount);
        Assert.AreEqual(0, viewModel.IssueCount);
    }

    [TestMethod]
    public async Task UnavailableEnabledSourceIsReportedWithoutBlockingAvailableSources()
    {
        using var temporary = new TemporaryDirectory();
        var available = temporary.CreateDirectory("available");
        var missing = Path.Combine(temporary.Path, "missing");
        await File.WriteAllTextAsync(Path.Combine(available, "one.package"), "same");
        await File.WriteAllTextAsync(Path.Combine(available, "two.package"), "same");
        var settings = new FakeSettingsStore(new DuplicateSettings(
            null,
            [
                new SavedScanSource(available, true),
                new SavedScanSource(missing, true),
            ]));
        var viewModel = CreateViewModel(settings);

        await viewModel.StartScanCommand.ExecuteAsync();

        Assert.AreEqual(1, viewModel.DuplicateGroupCount);
        Assert.AreEqual(1, viewModel.IssueCount);
    }

    [TestMethod]
    public async Task CancelledRunUpdatesStatusWithoutReportingAnUnexpectedError()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.CreateDirectory("source");
        var settings = new FakeSettingsStore(new DuplicateSettings(
            null,
            [new SavedScanSource(source, true)]));
        var errors = new FakeUnexpectedErrorHandler();
        var runService = new FakeDuplicateRunService((_, _, _) =>
            Task.FromCanceled<DuplicateReport>(new CancellationToken(canceled: true)));
        var viewModel = CreateViewModel(settings, runService: runService, errorHandler: errors);

        await viewModel.StartScanCommand.ExecuteAsync();

        Assert.AreEqual("扫描已取消", viewModel.StatusText);
        Assert.IsFalse(viewModel.IsBusy);
        Assert.AreEqual(0, errors.Errors.Count);
    }

    [TestMethod]
    public async Task UnexpectedRunFailureUsesTheInjectedErrorHandler()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.CreateDirectory("source");
        var settings = new FakeSettingsStore(new DuplicateSettings(
            null,
            [new SavedScanSource(source, true)]));
        var errors = new FakeUnexpectedErrorHandler();
        var expected = new InvalidOperationException("unexpected");
        var runService = new FakeDuplicateRunService((_, _, _) => Task.FromException<DuplicateReport>(expected));
        var viewModel = CreateViewModel(settings, runService: runService, errorHandler: errors);

        await viewModel.StartScanCommand.ExecuteAsync();

        Assert.IsFalse(viewModel.IsBusy);
        Assert.AreEqual(1, errors.Errors.Count);
        Assert.AreEqual("扫描没有完成", errors.Errors[0].Title);
        Assert.AreSame(expected, errors.Errors[0].Exception);
    }

    [TestMethod]
    public async Task AsyncRelayCommandReportsFailuresAndAlwaysBecomesExecutableAgain()
    {
        var failures = new List<Exception>();
        var command = new AsyncRelayCommand(
            () => Task.FromException(new InvalidOperationException("boom")),
            failures.Add);

        await command.ExecuteAsync();

        Assert.AreEqual(1, failures.Count);
        Assert.IsInstanceOfType<InvalidOperationException>(failures[0]);
        Assert.IsTrue(command.CanExecute(null));
    }

    [TestMethod]
    public async Task AsyncRelayCommandDoesNotReportCancellationAndDoesNotRunTwice()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var failures = new List<Exception>();
        var command = new AsyncRelayCommand(
            async () =>
            {
                calls++;
                started.SetResult();
                await release.Task;
            },
            failures.Add);

        var first = command.ExecuteAsync();
        await started.Task;
        await command.ExecuteAsync();
        release.SetResult();
        await first;

        Assert.AreEqual(1, calls);
        Assert.AreEqual(0, failures.Count);
        Assert.IsTrue(command.CanExecute(null));

        var cancelled = new AsyncRelayCommand(
            () => Task.FromCanceled(new CancellationToken(canceled: true)),
            failures.Add);
        await cancelled.ExecuteAsync();
        Assert.AreEqual(0, failures.Count);
        Assert.IsTrue(cancelled.CanExecute(null));
    }

    private static MainWindowViewModel CreateViewModel(
        FakeSettingsStore settings,
        FakeFolderPicker? picker = null,
        IDuplicateRunService? runService = null,
        FakeUnexpectedErrorHandler? errorHandler = null) => new(
        runService ?? new DuplicateRunService(DuplicateScanner.CreateDefault()),
        picker ?? new FakeFolderPicker(),
        settings,
        unexpectedErrorHandler: errorHandler ?? new FakeUnexpectedErrorHandler());

    private static bool PathsEqual(string first, string second) =>
        StringComparer.OrdinalIgnoreCase.Equals(first, second);

    private sealed class FakeSettingsStore(DuplicateSettings? settings) : IDuplicateSettingsStore
    {
        public int SaveCalls { get; private set; }

        public DuplicateSettings? LastSaved { get; private set; }

        public DuplicateSettings? Load() => settings;

        public void Save(DuplicateSettings value)
        {
            SaveCalls++;
            LastSaved = value;
        }
    }

    private sealed class FakeFolderPicker(params string[] paths) : IFolderPickerService
    {
        private readonly Queue<string> paths = new(paths);

        public string? PickFolder(string title, string? initialDirectory = null) =>
            paths.Count == 0 ? null : paths.Dequeue();
    }

    private sealed class FakeDuplicateRunService(
        Func<DuplicateRunInput, IProgress<DuplicateScanProgress>?, CancellationToken, Task<DuplicateReport>> run)
        : IDuplicateRunService
    {
        public Task<DuplicateReport> RunAsync(
            DuplicateRunInput input,
            IProgress<DuplicateScanProgress>? progress = null,
            CancellationToken cancellationToken = default) => run(input, progress, cancellationToken);
    }

    private sealed class FakeUnexpectedErrorHandler : IUnexpectedErrorHandler
    {
        public List<(string Title, Exception Exception)> Errors { get; } = [];

        public void Report(string title, Exception exception)
        {
            Errors.Add((title, exception));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"s4md-desktop-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateDirectory(params string[] parts)
        {
            var path = parts.Aggregate(Path, System.IO.Path.Combine);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
