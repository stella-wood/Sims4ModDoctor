using System.Text;
using Sims4ModDoctor.Core.Duplicates;

namespace Sims4ModDoctor.Core.Reporting;

public static class DuplicateReportFiles
{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static async Task WriteAsync(
        DuplicateReport report,
        string jsonPath,
        string htmlPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(htmlPath);

        var normalizedJsonPath = Path.GetFullPath(jsonPath);
        var normalizedHtmlPath = Path.GetFullPath(htmlPath);
        Directory.CreateDirectory(Path.GetDirectoryName(normalizedJsonPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(normalizedHtmlPath)!);

        await File.WriteAllTextAsync(
            normalizedJsonPath,
            DuplicateReportJson.Serialize(report),
            Utf8WithoutBom,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            normalizedHtmlPath,
            DuplicateHtmlReportRenderer.Render(report),
            Utf8WithoutBom,
            cancellationToken).ConfigureAwait(false);
    }
}
