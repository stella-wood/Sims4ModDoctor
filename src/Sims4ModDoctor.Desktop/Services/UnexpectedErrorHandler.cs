using System.IO;
using System.Windows;

namespace Sims4ModDoctor.Desktop.Services;

public interface IUnexpectedErrorHandler
{
    void Report(string title, Exception exception);
}

public sealed class UnexpectedErrorHandler : IUnexpectedErrorHandler
{
    private readonly string logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sims4ModDoctor",
        "application-error.log");

    public void Report(string title, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(exception);

        var logged = TryAppend(title, exception);
        var message = "发生了未预期的问题，请重试。";
        if (logged)
        {
            message += "\n\n详细信息已写入本地日志。";
        }

        MessageBox.Show(
            Application.Current?.MainWindow,
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private bool TryAppend(string title, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(
                logPath,
                $"[{DateTimeOffset.Now:O}] {title}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
