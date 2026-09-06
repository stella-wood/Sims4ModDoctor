using System.Windows;

namespace Sims4ModDoctor.Desktop.Services;

public interface IFileActionDialogService
{
    bool ConfirmDeletion(FileDeletionPreflight preflight);

    void ShowProblems(string title, IReadOnlyList<FileActionFailure> failures);
}

public sealed class FileActionDialogService : IFileActionDialogService
{
    public bool ConfirmDeletion(FileDeletionPreflight preflight)
    {
        var dialog = new DeleteConfirmationWindow()
        {
            Owner = Application.Current?.MainWindow,
        };
        return dialog.ShowDialog() == true;
    }

    public void ShowProblems(string title, IReadOnlyList<FileActionFailure> failures)
    {
        if (failures.Count == 0)
        {
            return;
        }

        var details = string.Join(
            Environment.NewLine,
            failures.Take(8).Select(failure =>
                string.IsNullOrWhiteSpace(failure.Path)
                    ? $"• {failure.Message}"
                    : $"• {failure.Path}{Environment.NewLine}  {failure.Message}"));
        if (failures.Count > 8)
        {
            details += $"{Environment.NewLine}…另有 {failures.Count - 8} 项";
        }

        MessageBox.Show(
            Application.Current?.MainWindow,
            details,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
