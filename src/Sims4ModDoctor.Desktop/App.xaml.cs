using System.IO;
using System.Windows;

namespace Sims4ModDoctor.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);

        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            TryWriteStartupFailure(exception);
            MessageBox.Show(
                $"Sims 4 Mod Doctor 启动失败。\n\n{exception.Message}",
                "无法启动",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private static void TryWriteStartupFailure(Exception exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Sims4ModDoctor");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "startup-error.log"), exception.ToString());
        }
        catch (Exception writeException) when (writeException is IOException or UnauthorizedAccessException)
        {
            // The original startup error is still shown to the user.
        }
    }
}
