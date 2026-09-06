using System.Windows;
using Sims4ModDoctor.Core.Duplicates;
using Sims4ModDoctor.Desktop.Services;
using Sims4ModDoctor.Desktop.ViewModels;

namespace Sims4ModDoctor.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel(
            DuplicateScanner.CreateDefault(),
            new FolderPickerService(),
            new DuplicateSettingsStore());
    }
}
