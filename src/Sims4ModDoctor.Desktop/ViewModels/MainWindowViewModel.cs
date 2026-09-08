using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using Sims4ModDoctor.Core.Duplicates;
using Sims4ModDoctor.Desktop.Infrastructure;
using Sims4ModDoctor.Desktop.Services;

namespace Sims4ModDoctor.Desktop.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly IDuplicateRunService duplicateRunService;
    private readonly DuplicateResultSession resultSession;
    private readonly IFolderPickerService folderPicker;
    private readonly IDuplicateSettingsStore settingsStore;
    private readonly IFileActionService fileActionService;
    private readonly IFileActionDialogService fileActionDialogs;
    private readonly IUnexpectedErrorHandler unexpectedErrorHandler;
    private CancellationTokenSource? scanCancellation;
    private IReadOnlyList<DuplicateGroup>? lastDeleteSnapshot;
    private int resultGeneration;
    private int lastDeleteResultGeneration;
    private bool isHomeVisible = true;
    private bool isScannerVisible;
    private bool isBusy;
    private bool isProgressIndeterminate;
    private string? modsRoot;
    private bool isModsRootAvailable;
    private bool isStatusVisible;
    private string statusText = string.Empty;
    private string statusDetail = string.Empty;
    private double progressValue;
    private double progressMaximum = 1;
    private int discoveredFileCount;
    private int issueCount;
    private string fileActionStatusText = string.Empty;

    public MainWindowViewModel(
        IDuplicateRunService duplicateRunService,
        IFolderPickerService folderPicker,
        IDuplicateSettingsStore settingsStore,
        IFileActionService? fileActionService = null,
        IFileActionDialogService? fileActionDialogs = null,
        IUnexpectedErrorHandler? unexpectedErrorHandler = null,
        DuplicateResultSession? resultSession = null)
    {
        this.duplicateRunService = duplicateRunService;
        this.resultSession = resultSession ?? new DuplicateResultSession();
        this.folderPicker = folderPicker;
        this.settingsStore = settingsStore;
        this.fileActionService = fileActionService ?? new FileActionService();
        this.fileActionDialogs = fileActionDialogs ?? new FileActionDialogService();
        this.unexpectedErrorHandler = unexpectedErrorHandler ?? new UnexpectedErrorHandler();
        this.resultSession.PropertyChanged += OnResultSessionPropertyChanged;

        Sources.CollectionChanged += OnSourcesChanged;
        OpenDuplicateScannerCommand = new RelayCommand(_ => ShowScanner());
        GoHomeCommand = new RelayCommand(_ => ShowHome(), _ => !IsBusy);
        AddSourceCommand = new RelayCommand(_ => AddSource(), _ => !IsBusy);
        ChangeModsRootCommand = new RelayCommand(_ => ChangeModsRoot(), _ => !IsBusy);
        RemoveSourceCommand = new RelayCommand(RemoveSource, _ => !IsBusy);
        StartScanCommand = new AsyncRelayCommand(
            StartScanAsync,
            exception => ReportUnexpectedError("扫描没有完成", exception),
            CanStartScan);
        CancelScanCommand = new RelayCommand(_ => scanCancellation?.Cancel(), _ => IsBusy);
        ApplySuggestionsCommand = new RelayCommand(_ => ApplySuggestions(), _ => Groups.Count > 0 && !IsBusy);
        ClearSelectionCommand = new RelayCommand(_ => ClearSelection(), _ => SelectedFileCount > 0 && !IsBusy);
        ToggleAllGroupsCommand = new RelayCommand(_ => ToggleAllGroups(), _ => Groups.Count > 0 && !IsBusy);
        DeleteSelectedCommand = new AsyncRelayCommand(
            DeleteSelectedAsync,
            exception => ReportUnexpectedError("删除没有完成", exception),
            () => SelectedFileCount > 0 && !IsBusy);
        UndoLastDeleteCommand = new AsyncRelayCommand(
            UndoLastDeleteAsync,
            exception => ReportUnexpectedError("撤回没有完成", exception),
            () => this.fileActionService.CanUndoLastDelete && !IsBusy);

        LoadSettings();
    }

    public ObservableCollection<ScanSourceViewModel> Sources { get; } = [];

    public IReadOnlyList<DuplicateGroupViewModel> Groups => resultSession.Groups;

    public RelayCommand OpenDuplicateScannerCommand { get; }

    public RelayCommand GoHomeCommand { get; }

    public RelayCommand AddSourceCommand { get; }

    public RelayCommand ChangeModsRootCommand { get; }

    public RelayCommand RemoveSourceCommand { get; }

    public AsyncRelayCommand StartScanCommand { get; }

    public RelayCommand CancelScanCommand { get; }

    public RelayCommand ApplySuggestionsCommand { get; }

    public RelayCommand ClearSelectionCommand { get; }

    public RelayCommand ToggleAllGroupsCommand { get; }

    public AsyncRelayCommand DeleteSelectedCommand { get; }

    public AsyncRelayCommand UndoLastDeleteCommand { get; }

    public bool IsHomeVisible
    {
        get => isHomeVisible;
        private set => SetProperty(ref isHomeVisible, value);
    }

    public bool IsScannerVisible
    {
        get => isScannerVisible;
        private set => SetProperty(ref isScannerVisible, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!SetProperty(ref isBusy, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanEditSources));
            RefreshCommands();
        }
    }

    public bool IsProgressIndeterminate
    {
        get => isProgressIndeterminate;
        private set => SetProperty(ref isProgressIndeterminate, value);
    }

    public bool IsStatusVisible
    {
        get => isStatusVisible;
        private set => SetProperty(ref isStatusVisible, value);
    }

    public string? ModsRoot
    {
        get => modsRoot;
        private set
        {
            if (SetProperty(ref modsRoot, value))
            {
                OnPropertyChanged(nameof(ModsRootDisplay));
                OnPropertyChanged(nameof(HasModsRoot));
                OnPropertyChanged(nameof(ModsRootActionText));
            }
        }
    }

    public string ModsRootDisplay => string.IsNullOrWhiteSpace(ModsRoot) ? "未设置" : ModsRoot;

    public bool HasModsRoot => !string.IsNullOrWhiteSpace(ModsRoot);

    public string ModsRootActionText => HasModsRoot ? "更改" : "选择";

    public bool IsModsRootAvailable
    {
        get => isModsRootAvailable;
        private set
        {
            if (SetProperty(ref isModsRootAvailable, value))
            {
                OnPropertyChanged(nameof(HasModsRootAvailabilityWarning));
            }
        }
    }

    public bool HasModsRootAvailabilityWarning => HasModsRoot && !IsModsRootAvailable;

    public string StatusText
    {
        get => statusText;
        private set => SetProperty(ref statusText, value);
    }

    public string StatusDetail
    {
        get => statusDetail;
        private set => SetProperty(ref statusDetail, value);
    }

    public double ProgressValue
    {
        get => progressValue;
        private set => SetProperty(ref progressValue, value);
    }

    public double ProgressMaximum
    {
        get => progressMaximum;
        private set => SetProperty(ref progressMaximum, value);
    }

    public int DiscoveredFileCount
    {
        get => discoveredFileCount;
        private set => SetProperty(ref discoveredFileCount, value);
    }

    public int IssueCount
    {
        get => issueCount;
        private set => SetProperty(ref issueCount, value);
    }

    public bool HasResults => resultSession.HasResults;

    public bool CanEditSources => !IsBusy;

    public bool HasNoResults => resultSession.HasNoResults;

    public int DuplicateGroupCount => resultSession.DuplicateGroupCount;

    public int DuplicateFileCount => resultSession.DuplicateFileCount;

    public int SelectedFileCount => resultSession.SelectedFileCount;

    public string DuplicateSizeText => resultSession.DuplicateSizeText;

    public string SelectedSizeText => resultSession.SelectedSizeText;

    public bool ShowExpandAllIcon => resultSession.ShowExpandAllIcon;

    public bool ShowCollapseAllIcon => resultSession.ShowCollapseAllIcon;

    public string ExpandCollapseToolTip => resultSession.ExpandCollapseToolTip;

    public string FileActionStatusText
    {
        get => fileActionStatusText;
        private set
        {
            if (SetProperty(ref fileActionStatusText, value))
            {
                OnPropertyChanged(nameof(HasFileActionStatus));
            }
        }
    }

    public bool HasFileActionStatus => !string.IsNullOrWhiteSpace(FileActionStatusText);

    public bool CanUndoLastDelete => fileActionService.CanUndoLastDelete;

    private void LoadSettings()
    {
        var settings = settingsStore.Load();
        if (settings is not null)
        {
            ModsRoot = NormalizePathOrNull(settings.ModsRoot);
            foreach (var source in settings.Sources)
            {
                AddSource(source.Path, source.Enabled, saveSettings: false);
            }
        }

        if (Sources.Count == 0)
        {
            var defaultModsRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Electronic Arts",
                "The Sims 4",
                "Mods");

            if (Directory.Exists(defaultModsRoot))
            {
                ModsRoot = defaultModsRoot;
                AddSource(defaultModsRoot, saveSettings: false);
            }
        }

        RefreshAvailability();
    }

    private void ShowScanner()
    {
        RefreshAvailability();
        IsHomeVisible = false;
        IsScannerVisible = true;
    }

    private void ShowHome()
    {
        IsScannerVisible = false;
        IsHomeVisible = true;
    }

    private void AddSource()
    {
        var path = folderPicker.PickFolder("选择要检查的文件夹", ModsRoot);
        if (path is not null)
        {
            AddSource(path);
        }
    }

    private void ChangeModsRoot()
    {
        var path = folderPicker.PickFolder("选择当前游戏的 Mods 文件夹", ModsRoot);
        var normalizedPath = NormalizePathOrNull(path);
        if (normalizedPath is null)
        {
            return;
        }

        ModsRoot = normalizedPath;
        var source = Sources.FirstOrDefault(item => PathRulesEqual(item.Path, normalizedPath));
        if (source is null)
        {
            AddSource(normalizedPath, saveSettings: false);
        }
        else
        {
            source.IsEnabled = true;
        }

        RefreshAvailability();
        SaveSettings();
    }

    private void AddSource(string path, bool enabled = true, bool saveSettings = true)
    {
        var normalizedPath = NormalizePathOrNull(path);
        if (normalizedPath is null
            || Sources.Any(source => PathRulesEqual(source.Path, normalizedPath)))
        {
            return;
        }

        var source = new ScanSourceViewModel(normalizedPath, enabled);
        source.PropertyChanged += OnSourcePropertyChanged;
        Sources.Add(source);

        if (saveSettings)
        {
            SaveSettings();
        }
    }

    private void RemoveSource(object? parameter)
    {
        if (parameter is not ScanSourceViewModel source)
        {
            return;
        }

        source.PropertyChanged -= OnSourcePropertyChanged;
        Sources.Remove(source);
        SaveSettings();
    }

    private bool CanStartScan() => !IsBusy && Sources.Any(source => source.IsEnabled);

    private async Task StartScanAsync()
    {
        RefreshAvailability();
        var enabledSources = Sources.Where(source => source.IsEnabled).ToArray();
        if (enabledSources.Length == 0)
        {
            return;
        }

        SaveSettings();

        var input = new DuplicateRunInput(
            enabledSources
                .Select(source => new DuplicateRunSource(source.Path, source.Name))
                .ToArray(),
            ModsRoot);

        ResetResultSession();
        scanCancellation = new CancellationTokenSource();
        IsBusy = true;
        IsProgressIndeterminate = true;
        IsStatusVisible = true;
        StatusText = "正在准备扫描…";
        StatusDetail = string.Empty;

        var progress = new Progress<DuplicateScanProgress>(UpdateProgress);

        try
        {
            var report = await Task.Run(
                () => duplicateRunService.RunAsync(input, progress, scanCancellation.Token),
                scanCancellation.Token);

            StatusText = "正在整理结果…";
            IsProgressIndeterminate = true;
            var resultGroups = await Task.Run(
                () => resultSession.PrepareGroups(report.Groups, scanCancellation.Token),
                scanCancellation.Token);
            resultSession.Replace(resultGroups);

            IssueCount = report.Issues.Count;
            DiscoveredFileCount = report.DiscoveredFileCount;
            StatusText = report.Groups.Count == 0
                ? "扫描完成，没有发现重复文件"
                : $"扫描完成，发现 {report.Groups.Count} 组重复文件";
            StatusDetail = report.Issues.Count == 0
                ? $"共查看 {report.DiscoveredFileCount:N0} 个文件，结果完整。"
                : $"共查看 {report.DiscoveredFileCount:N0} 个文件；有 {report.Issues.Count} 项未能读取，可重新扫描。";
        }
        catch (OperationCanceledException)
        {
            StatusText = "扫描已取消";
            StatusDetail = string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText = "扫描没有完成";
            StatusDetail = exception.Message;
        }
        finally
        {
            scanCancellation.Dispose();
            scanCancellation = null;
            IsBusy = false;
            IsProgressIndeterminate = false;
            RefreshResultProperties();
        }
    }

    private void UpdateProgress(DuplicateScanProgress progress)
    {
        StatusText = progress.Message;
        DiscoveredFileCount = progress.DiscoveredFileCount;
        IsProgressIndeterminate = progress.TotalItems is null || progress.Phase == DuplicateScanPhase.Discovering;
        if (progress.TotalItems is int total)
        {
            ProgressMaximum = Math.Max(1, total);
            ProgressValue = Math.Min(progress.CompletedItems, ProgressMaximum);
        }

        StatusDetail = progress.Phase switch
        {
            DuplicateScanPhase.Discovering => $"已经找到 {progress.DiscoveredFileCount:N0} 个候选文件",
            DuplicateScanPhase.Hashing => string.Empty,
            _ => StatusDetail,
        };
    }

    private void ApplySuggestions() => resultSession.ApplySuggestions();

    private void ClearSelection() => resultSession.ClearSelection();

    private void ToggleAllGroups() => resultSession.ToggleAllGroups();

    private Task DeleteSelectedAsync() => DeleteSelectedCoreAsync();

    private async Task DeleteSelectedCoreAsync()
    {
        var selectedFiles = Groups
            .SelectMany(group => group.Files
                .Where(file => file.IsSelected)
                .Select(file => new FileDeletionCandidate(
                    file.Path,
                    file.Size,
                    file.LastWriteTimeUtc,
                    group.GroupLabel)))
            .ToArray();
        var fullySelectedGroups = Groups
            .Where(group => group.SelectedCount == group.Files.Count)
            .Select(group => group.GroupLabel)
            .ToArray();
        var preflight = fileActionService.Preflight(selectedFiles, fullySelectedGroups);

        if (!preflight.CanExecute)
        {
            fileActionDialogs.ShowProblems("暂时不能删除", preflight.Problems);
            return;
        }

        if (!fileActionDialogs.ConfirmDeletion(preflight))
        {
            return;
        }

        var snapshot = Groups.Select(group => group.Model).ToArray();
        IsBusy = true;
        try
        {
            var result = await fileActionService.DeleteAsync(preflight);
            if (result.CompletedPaths.Count > 0)
            {
                lastDeleteSnapshot = snapshot;
                lastDeleteResultGeneration = resultGeneration;
                resultSession.RemoveCompletedPaths(
                    snapshot,
                    result.CompletedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase));
                FileActionStatusText = $"已移入回收站 {result.CompletedPaths.Count} 个文件";
            }

            if (result.Failures.Count > 0)
            {
                fileActionDialogs.ShowProblems("部分文件未能删除", result.Failures);
            }
        }
        finally
        {
            IsBusy = false;
            RefreshResultProperties();
        }
    }

    private Task UndoLastDeleteAsync() => UndoLastDeleteCoreAsync();

    private async Task UndoLastDeleteCoreAsync()
    {
        if (!fileActionService.CanUndoLastDelete)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await fileActionService.UndoLastDeleteAsync();
            if (result.CompletedPaths.Count > 0)
            {
                if (lastDeleteSnapshot is not null && lastDeleteResultGeneration == resultGeneration)
                {
                    resultSession.RestoreExistingPaths(lastDeleteSnapshot);
                }

                FileActionStatusText = result.Failures.Count == 0
                    ? $"已撤回，恢复 {result.CompletedPaths.Count} 个文件"
                    : $"已恢复 {result.CompletedPaths.Count} 个文件，部分项目仍需处理";
            }

            if (result.Failures.Count > 0)
            {
                fileActionDialogs.ShowProblems("部分文件未能恢复", result.Failures);
            }

            if (!fileActionService.CanUndoLastDelete)
            {
                lastDeleteSnapshot = null;
            }
        }
        finally
        {
            IsBusy = false;
            RefreshResultProperties();
        }
    }

    private void OnSourcesChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        if (Sources.Count == 0)
        {
            ResetResultSession();
        }

        OnPropertyChanged(nameof(Sources));
        StartScanCommand?.RaiseCanExecuteChanged();
    }

    private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(ScanSourceViewModel.IsEnabled))
        {
            StartScanCommand.RaiseCanExecuteChanged();
            SaveSettings();
        }
    }

    private void RefreshAvailability()
    {
        foreach (var source in Sources)
        {
            source.RefreshAvailability();
        }

        IsModsRootAvailable = HasModsRoot && Directory.Exists(ModsRoot!);
    }

    private void SaveSettings() => settingsStore.Save(new DuplicateSettings(
        ModsRoot,
        Sources.Select(source => new SavedScanSource(source.Path, source.IsEnabled)).ToArray()));

    private static string? NormalizePathOrNull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : Path.TrimEndingDirectorySeparator(fullPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool PathRulesEqual(string first, string second) =>
        StringComparer.OrdinalIgnoreCase.Equals(first, second);

    private void ReportUnexpectedError(string title, Exception exception)
    {
        IsBusy = false;
        unexpectedErrorHandler.Report(title, exception);
        RefreshResultProperties();
    }

    private void OnResultSessionPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (string.IsNullOrWhiteSpace(eventArgs.PropertyName))
        {
            OnPropertyChanged(null);
        }
        else
        {
            OnPropertyChanged(eventArgs.PropertyName);
        }

        RefreshResultCommands();
    }

    private void ResetResultSession()
    {
        resultGeneration++;
        ClearFileActionHistory();
        resultSession.Reset();
        IssueCount = 0;
        DiscoveredFileCount = 0;
        ProgressValue = 0;
        ProgressMaximum = 1;
        IsProgressIndeterminate = false;
        IsStatusVisible = false;
        StatusText = string.Empty;
        StatusDetail = string.Empty;
    }

    private void ClearFileActionHistory()
    {
        fileActionService.ClearLastDelete();
        lastDeleteSnapshot = null;
        FileActionStatusText = string.Empty;
        OnPropertyChanged(nameof(CanUndoLastDelete));
        UndoLastDeleteCommand.RaiseCanExecuteChanged();
    }

    private void RefreshResultProperties()
    {
        OnPropertyChanged(nameof(CanUndoLastDelete));
        RefreshResultCommands();
    }

    private void RefreshResultCommands()
    {
        ApplySuggestionsCommand.RaiseCanExecuteChanged();
        ClearSelectionCommand.RaiseCanExecuteChanged();
        ToggleAllGroupsCommand.RaiseCanExecuteChanged();
        DeleteSelectedCommand.RaiseCanExecuteChanged();
        UndoLastDeleteCommand.RaiseCanExecuteChanged();
    }

    private void RefreshCommands()
    {
        GoHomeCommand.RaiseCanExecuteChanged();
        AddSourceCommand.RaiseCanExecuteChanged();
        ChangeModsRootCommand.RaiseCanExecuteChanged();
        RemoveSourceCommand.RaiseCanExecuteChanged();
        StartScanCommand.RaiseCanExecuteChanged();
        CancelScanCommand.RaiseCanExecuteChanged();
        RefreshResultCommands();
    }
}
