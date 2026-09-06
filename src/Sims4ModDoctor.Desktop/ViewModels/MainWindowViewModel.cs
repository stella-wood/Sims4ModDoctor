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
    private readonly DuplicateScanner scanner;
    private readonly IFolderPickerService folderPicker;
    private readonly DuplicateSettingsStore settingsStore;
    private readonly IFileActionService fileActionService;
    private readonly IFileActionDialogService fileActionDialogs;
    private readonly DuplicateSelectionService selectionService = new();
    private CancellationTokenSource? scanCancellation;
    private IReadOnlyList<DuplicateGroupViewModel> groups = Array.Empty<DuplicateGroupViewModel>();
    private IReadOnlyList<DuplicateGroup>? lastDeleteSnapshot;
    private int resultGeneration;
    private int lastDeleteResultGeneration;
    private bool isBulkUpdatingSelection;
    private bool isBulkUpdatingExpansion;
    private bool isHomeVisible = true;
    private bool isScannerVisible;
    private bool isBusy;
    private bool isProgressIndeterminate;
    private string? modsRoot;
    private bool isStatusVisible;
    private string statusText = string.Empty;
    private string statusDetail = string.Empty;
    private double progressValue;
    private double progressMaximum = 1;
    private int discoveredFileCount;
    private int issueCount;
    private string fileActionStatusText = string.Empty;

    public MainWindowViewModel(
        DuplicateScanner scanner,
        IFolderPickerService folderPicker,
        DuplicateSettingsStore settingsStore,
        IFileActionService? fileActionService = null,
        IFileActionDialogService? fileActionDialogs = null)
    {
        this.scanner = scanner;
        this.folderPicker = folderPicker;
        this.settingsStore = settingsStore;
        this.fileActionService = fileActionService ?? new FileActionService();
        this.fileActionDialogs = fileActionDialogs ?? new FileActionDialogService();

        Sources.CollectionChanged += OnSourcesChanged;
        OpenDuplicateScannerCommand = new RelayCommand(_ => ShowScanner());
        GoHomeCommand = new RelayCommand(_ => ShowHome(), _ => !IsBusy);
        AddSourceCommand = new RelayCommand(_ => AddSource(), _ => !IsBusy);
        RemoveSourceCommand = new RelayCommand(RemoveSource, _ => !IsBusy);
        StartScanCommand = new AsyncRelayCommand(StartScanAsync, CanStartScan);
        CancelScanCommand = new RelayCommand(_ => scanCancellation?.Cancel(), _ => IsBusy);
        ApplySuggestionsCommand = new RelayCommand(_ => ApplySuggestions(), _ => Groups.Count > 0 && !IsBusy);
        ClearSelectionCommand = new RelayCommand(_ => ClearSelection(), _ => SelectedFileCount > 0 && !IsBusy);
        ToggleAllGroupsCommand = new RelayCommand(_ => ToggleAllGroups(), _ => Groups.Count > 0 && !IsBusy);
        DeleteSelectedCommand = new AsyncRelayCommand(DeleteSelectedAsync, () => SelectedFileCount > 0 && !IsBusy);
        UndoLastDeleteCommand = new AsyncRelayCommand(UndoLastDeleteAsync, () => this.fileActionService.CanUndoLastDelete && !IsBusy);

        LoadSettings();
    }

    public ObservableCollection<ScanSourceViewModel> Sources { get; } = [];

    public IReadOnlyList<DuplicateGroupViewModel> Groups
    {
        get => groups;
        private set => SetProperty(ref groups, value);
    }

    public RelayCommand OpenDuplicateScannerCommand { get; }

    public RelayCommand GoHomeCommand { get; }

    public RelayCommand AddSourceCommand { get; }

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
        private set => SetProperty(ref modsRoot, value);
    }

    public string ModsRootDisplay => string.IsNullOrWhiteSpace(ModsRoot) ? "未检测到" : ModsRoot;

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

    public bool HasResults => Groups.Count > 0;

    public bool CanEditSources => !IsBusy;

    public bool HasNoResults => !HasResults;

    public int DuplicateGroupCount => Groups.Count;

    public int DuplicateFileCount => Groups.Sum(group => group.Files.Count);

    public int SelectedFileCount => Groups.Sum(group => group.SelectedCount);

    public string DuplicateSizeText => DuplicateGroupViewModel.FormatBytes(
        Groups.Sum(group => checked(group.Model.FileSize * group.Files.Count)));

    public string SelectedSizeText => DuplicateGroupViewModel.FormatBytes(
        Groups.Sum(group => group.SelectedBytes));

    public bool ShowExpandAllIcon => Groups.Count == 0 || !Groups.All(group => group.IsExpanded);

    public bool ShowCollapseAllIcon => Groups.Count > 0 && Groups.All(group => group.IsExpanded);

    public string ExpandCollapseToolTip => Groups.Count > 0 && Groups.All(group => group.IsExpanded)
        ? "收起全部"
        : "展开全部";

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
            ModsRoot = !string.IsNullOrWhiteSpace(settings.ModsRoot) && Directory.Exists(settings.ModsRoot)
                ? settings.ModsRoot
                : null;
            foreach (var source in settings.Sources.Where(source => Directory.Exists(source.Path)))
            {
                AddSource(source.Path, source.Enabled);
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
                AddSource(defaultModsRoot);
            }
        }

        OnPropertyChanged(nameof(ModsRootDisplay));
    }

    private void ShowScanner()
    {
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

    private void AddSource(string path, bool enabled = true)
    {
        if (Sources.Any(source => StringComparer.OrdinalIgnoreCase.Equals(source.Path, path)))
        {
            return;
        }

        var source = new ScanSourceViewModel(path, enabled);
        source.PropertyChanged += OnSourcePropertyChanged;
        Sources.Add(source);

        if (string.IsNullOrWhiteSpace(ModsRoot)
            && StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                "Mods"))
        {
            ModsRoot = path;
            OnPropertyChanged(nameof(ModsRootDisplay));
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
    }

    private bool CanStartScan() => !IsBusy && Sources.Any(source => source.IsEnabled);

    private async Task StartScanAsync()
    {
        var enabledSources = Sources.Where(source => source.IsEnabled).ToArray();
        if (enabledSources.Length == 0)
        {
            return;
        }

        settingsStore.Save(new DuplicateSettings(
            ModsRoot,
            Sources.Select(source => new SavedScanSource(source.Path, source.IsEnabled)).ToArray()));

        var request = new DuplicateScanRequest(
            enabledSources.Select((source, index) => new ScanSource(
                $"目录 {index + 1}",
                source.Path,
                Order: index,
                Label: source.Name)).ToArray(),
            ModsRoot,
            [".package", ".ts4script"]);

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
                () => scanner.ScanAsync(request, progress, scanCancellation.Token),
                scanCancellation.Token);

            StatusText = "正在整理结果…";
            IsProgressIndeterminate = true;
            var resultGroups = await Task.Run(
                () => BuildGroupViewModels(report.Groups, scanCancellation.Token),
                scanCancellation.Token);
            SetGroups(resultGroups);

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

    private void ApplySuggestions()
    {
        isBulkUpdatingSelection = true;
        try
        {
            foreach (var group in Groups)
            {
                group.ApplySuggestion();
            }
        }
        finally
        {
            isBulkUpdatingSelection = false;
        }

        RefreshResultProperties();
    }

    private void ClearSelection()
    {
        isBulkUpdatingSelection = true;
        try
        {
            foreach (var group in Groups)
            {
                group.ClearSelection();
            }
        }
        finally
        {
            isBulkUpdatingSelection = false;
        }

        RefreshResultProperties();
    }

    private void ToggleAllGroups()
    {
        var expand = !Groups.All(group => group.IsExpanded);
        isBulkUpdatingExpansion = true;
        try
        {
            foreach (var group in Groups)
            {
                group.IsExpanded = expand;
            }
        }
        finally
        {
            isBulkUpdatingExpansion = false;
        }

        RefreshExpansionProperties();
    }

    private async Task DeleteSelectedAsync()
    {
        try
        {
            await DeleteSelectedCoreAsync();
        }
        catch (Exception exception)
        {
            IsBusy = false;
            fileActionDialogs.ShowProblems("删除没有完成", [new FileActionFailure(string.Empty, exception.Message)]);
            RefreshResultProperties();
        }
    }

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
                RebuildGroups(snapshot, result.CompletedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase));
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

    private async Task UndoLastDeleteAsync()
    {
        try
        {
            await UndoLastDeleteCoreAsync();
        }
        catch (Exception exception)
        {
            IsBusy = false;
            fileActionDialogs.ShowProblems("撤回没有完成", [new FileActionFailure(string.Empty, exception.Message)]);
            RefreshResultProperties();
        }
    }

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
                    RebuildGroupsFromExistingFiles(lastDeleteSnapshot);
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
        }
    }

    private void OnResultFilePropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(DuplicateFileViewModel.IsSelected))
        {
            if (isBulkUpdatingSelection)
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedFileCount));
            OnPropertyChanged(nameof(SelectedSizeText));
            ClearSelectionCommand.RaiseCanExecuteChanged();
            DeleteSelectedCommand.RaiseCanExecuteChanged();
        }
    }

    private void OnResultGroupPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(DuplicateGroupViewModel.IsExpanded))
        {
            if (isBulkUpdatingExpansion)
            {
                return;
            }

            RefreshExpansionProperties();
        }
    }

    private static IReadOnlyList<DuplicateGroupViewModel> BuildGroupViewModels(
        IReadOnlyList<DuplicateGroup> reportGroups,
        CancellationToken cancellationToken)
    {
        var result = new DuplicateGroupViewModel[reportGroups.Count];
        for (var index = 0; index < reportGroups.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result[index] = new DuplicateGroupViewModel(reportGroups[index], index + 1);
        }

        return result;
    }

    private void SetGroups(IReadOnlyList<DuplicateGroupViewModel> value)
    {
        foreach (var group in Groups)
        {
            group.PropertyChanged -= OnResultGroupPropertyChanged;
            foreach (var file in group.Files)
            {
                file.PropertyChanged -= OnResultFilePropertyChanged;
            }
        }

        foreach (var group in value)
        {
            group.PropertyChanged += OnResultGroupPropertyChanged;
            foreach (var file in group.Files)
            {
                file.PropertyChanged += OnResultFilePropertyChanged;
            }
        }

        Groups = value;
        RefreshResultProperties();
    }

    private void RebuildGroups(
        IReadOnlyList<DuplicateGroup> snapshot,
        IReadOnlySet<string> excludedPaths)
    {
        var models = snapshot
            .Select(group => RebuildGroup(
                group,
                group.Files.Where(file => !excludedPaths.Contains(file.Path)).ToArray()))
            .Where(group => group is not null)
            .Cast<DuplicateGroup>()
            .ToArray();
        SetGroups(BuildGroupViewModels(models, CancellationToken.None));
    }

    private void RebuildGroupsFromExistingFiles(IReadOnlyList<DuplicateGroup> snapshot)
    {
        var models = snapshot
            .Select(group => RebuildGroup(
                group,
                group.Files.Where(file => File.Exists(file.Path)).ToArray()))
            .Where(group => group is not null)
            .Cast<DuplicateGroup>()
            .ToArray();
        SetGroups(BuildGroupViewModels(models, CancellationToken.None));
    }

    private DuplicateGroup? RebuildGroup(DuplicateGroup source, IReadOnlyList<DuplicateFile> remainingFiles)
    {
        if (remainingFiles.Count < 2)
        {
            return null;
        }

        var selection = selectionService.Select(remainingFiles.ToArray());
        return new DuplicateGroup(
            source.Sha256,
            source.FileSize,
            source.Section,
            remainingFiles,
            selection.KeepPath,
            selection.DeletePaths);
    }

    private void ResetResultSession()
    {
        resultGeneration++;
        ClearFileActionHistory();
        SetGroups(Array.Empty<DuplicateGroupViewModel>());
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
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasNoResults));
        OnPropertyChanged(nameof(DuplicateGroupCount));
        OnPropertyChanged(nameof(DuplicateFileCount));
        OnPropertyChanged(nameof(SelectedFileCount));
        OnPropertyChanged(nameof(DuplicateSizeText));
        OnPropertyChanged(nameof(SelectedSizeText));
        OnPropertyChanged(nameof(CanUndoLastDelete));
        RefreshExpansionProperties();
        ApplySuggestionsCommand.RaiseCanExecuteChanged();
        ClearSelectionCommand.RaiseCanExecuteChanged();
        ToggleAllGroupsCommand.RaiseCanExecuteChanged();
        DeleteSelectedCommand.RaiseCanExecuteChanged();
        UndoLastDeleteCommand.RaiseCanExecuteChanged();
    }

    private void RefreshExpansionProperties()
    {
        OnPropertyChanged(nameof(ShowExpandAllIcon));
        OnPropertyChanged(nameof(ShowCollapseAllIcon));
        OnPropertyChanged(nameof(ExpandCollapseToolTip));
    }

    private void RefreshCommands()
    {
        GoHomeCommand.RaiseCanExecuteChanged();
        AddSourceCommand.RaiseCanExecuteChanged();
        RemoveSourceCommand.RaiseCanExecuteChanged();
        StartScanCommand.RaiseCanExecuteChanged();
        CancelScanCommand.RaiseCanExecuteChanged();
        ApplySuggestionsCommand.RaiseCanExecuteChanged();
        ClearSelectionCommand.RaiseCanExecuteChanged();
        ToggleAllGroupsCommand.RaiseCanExecuteChanged();
        DeleteSelectedCommand.RaiseCanExecuteChanged();
        UndoLastDeleteCommand.RaiseCanExecuteChanged();
    }
}
