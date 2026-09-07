using System.IO;
using Sims4ModDoctor.Desktop.Infrastructure;

namespace Sims4ModDoctor.Desktop.ViewModels;

public sealed class ScanSourceViewModel(string path, bool isEnabled = true) : ObservableObject
{
    private bool isEnabled = isEnabled;
    private bool isAvailable = Directory.Exists(path);

    public string Path { get; } = path;

    public string Name => System.IO.Path.GetFileName(Path.TrimEnd(
        System.IO.Path.DirectorySeparatorChar,
        System.IO.Path.AltDirectorySeparatorChar));

    public bool IsEnabled
    {
        get => isEnabled;
        set => SetProperty(ref isEnabled, value);
    }

    public bool IsAvailable
    {
        get => isAvailable;
        private set
        {
            if (SetProperty(ref isAvailable, value))
            {
                OnPropertyChanged(nameof(HasAvailabilityWarning));
                OnPropertyChanged(nameof(AvailabilityText));
            }
        }
    }

    public bool HasAvailabilityWarning => !IsAvailable;

    public string AvailabilityText => IsAvailable ? string.Empty : "不可访问";

    public void RefreshAvailability() => IsAvailable = Directory.Exists(Path);
}
