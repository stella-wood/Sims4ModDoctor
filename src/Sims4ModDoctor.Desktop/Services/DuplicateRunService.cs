using Sims4ModDoctor.Core.Duplicates;

namespace Sims4ModDoctor.Desktop.Services;

public sealed record DuplicateRunSource(string Path, string Label);

public sealed record DuplicateRunInput(
    IReadOnlyList<DuplicateRunSource> Sources,
    string? ModsRoot = null);

public interface IDuplicateRunService
{
    Task<DuplicateReport> RunAsync(
        DuplicateRunInput input,
        IProgress<DuplicateScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class DuplicateRunService(DuplicateScanner scanner) : IDuplicateRunService
{
    private static readonly string[] IncludedExtensions = [".package", ".ts4script"];

    public Task<DuplicateReport> RunAsync(
        DuplicateRunInput input,
        IProgress<DuplicateScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Sources);

        var sources = input.Sources
            .Select((source, index) => new ScanSource(
                $"目录 {index + 1}",
                source.Path,
                Order: index,
                Label: source.Label))
            .ToArray();

        return scanner.ScanAsync(
            new DuplicateScanRequest(sources, input.ModsRoot, IncludedExtensions),
            progress,
            cancellationToken);
    }
}
