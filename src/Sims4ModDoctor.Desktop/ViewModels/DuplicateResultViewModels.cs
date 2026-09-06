using System.ComponentModel;
using Sims4ModDoctor.Core.Duplicates;
using Sims4ModDoctor.Desktop.Infrastructure;

namespace Sims4ModDoctor.Desktop.ViewModels;

public sealed class DuplicateFileViewModel(DuplicateFile file, bool isSuggestedKeep) : ObservableObject
{
    private bool isSelected;

    public string Path { get; } = file.Path;

    public string FileName { get; } = System.IO.Path.GetFileName(file.Path);

    public long Size { get; } = file.Size;

    public DateTime LastWriteTimeUtc { get; } = file.LastWriteTimeUtc;

    public bool IsSuggestedKeep { get; } = isSuggestedKeep;

    public bool IsSelected
    {
        get => isSelected;
        set => SetProperty(ref isSelected, value);
    }
}

public sealed class DuplicateGroupViewModel : ObservableObject
{
    private readonly HashSet<string> suggestedDeletePaths;
    private bool isExpanded;

    public DuplicateGroupViewModel(DuplicateGroup group, int index)
    {
        Model = group;
        Index = index;
        suggestedDeletePaths = group.SuggestedDeletePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Files = group.Files
            .OrderByDescending(file => StringComparer.OrdinalIgnoreCase.Equals(file.Path, group.SuggestedKeepPath))
            .Select(file => new DuplicateFileViewModel(
                file,
                StringComparer.OrdinalIgnoreCase.Equals(file.Path, group.SuggestedKeepPath)))
            .ToArray();
        isExpanded = index == 1;

        foreach (var file in Files)
        {
            file.PropertyChanged += OnFilePropertyChanged;
        }
    }

    public DuplicateGroup Model { get; }

    public int Index { get; }

    public IReadOnlyList<DuplicateFileViewModel> Files { get; }

    public bool IsExpanded
    {
        get => isExpanded;
        set => SetProperty(ref isExpanded, value);
    }

    public bool IsSuggestionSelected
    {
        get => Files.All(file => file.IsSelected)
            || Files.All(file => file.IsSelected == suggestedDeletePaths.Contains(file.Path));
        set
        {
            if (value)
            {
                ApplySuggestion();
            }
            else
            {
                ClearSelection();
            }

            OnPropertyChanged();
        }
    }

    public string GroupLabel => $"第 {Index} 组";

    public string Title => $"{Files.Count} 份相同";

    public string Detail => $"{FormatBytes(Model.FileSize)} / 份 · 可清理 {FormatBytes(Model.ReclaimableBytes)}";

    public string SectionTitle => Model.Section switch
    {
        DuplicateSection.FocusCrossScope => "重点目录与其他位置重复",
        DuplicateSection.FocusInternal => "重点目录内部重复",
        DuplicateSection.ReferenceInternal => "参照目录内部重复",
        _ => "普通重复",
    };

    public bool HasSectionLabel => Model.Section != DuplicateSection.Ordinary;

    public int SelectedCount => Files.Count(file => file.IsSelected);

    public long SelectedBytes => Files.Where(file => file.IsSelected).Sum(file => file.Size);

    public void ApplySuggestion()
    {
        foreach (var file in Files)
        {
            file.IsSelected = suggestedDeletePaths.Contains(file.Path);
        }
    }

    public void ClearSelection()
    {
        foreach (var file in Files)
        {
            file.IsSelected = false;
        }
    }

    private void OnFilePropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(DuplicateFileViewModel.IsSelected))
        {
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(SelectedBytes));
            OnPropertyChanged(nameof(IsSuggestionSelected));
        }
    }

    internal static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes:N0} B" : $"{size:0.##} {units[unit]}";
    }
}
