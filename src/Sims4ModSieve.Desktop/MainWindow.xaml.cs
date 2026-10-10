using System.Windows;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Desktop.Services;
using Sims4ModSieve.Desktop.ViewModels;

namespace Sims4ModSieve.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel(
            new DuplicateRunService(DuplicateScanner.CreateDefault()),
            new FolderPickerService(),
            new DuplicateSettingsStore());
    }
}
