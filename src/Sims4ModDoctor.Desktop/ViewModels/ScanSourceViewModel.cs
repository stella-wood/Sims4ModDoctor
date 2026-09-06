using Sims4ModDoctor.Desktop.Infrastructure;

namespace Sims4ModDoctor.Desktop.ViewModels;

public sealed class ScanSourceViewModel(string path, bool isEnabled = true) : ObservableObject
{
    private bool isEnabled = isEnabled;

    public string Path { get; } = path;

    public string Name => System.IO.Path.GetFileName(Path.TrimEnd(
        System.IO.Path.DirectorySeparatorChar,
        System.IO.Path.AltDirectorySeparatorChar));

    public bool IsEnabled
    {
        get => isEnabled;
        set => SetProperty(ref isEnabled, value);
    }
}
