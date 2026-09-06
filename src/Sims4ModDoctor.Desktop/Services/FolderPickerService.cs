using System.IO;
using Microsoft.Win32;

namespace Sims4ModDoctor.Desktop.Services;

public interface IFolderPickerService
{
    string? PickFolder(string title, string? initialDirectory = null);
}

public sealed class FolderPickerService : IFolderPickerService
{
    public string? PickFolder(string title, string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
