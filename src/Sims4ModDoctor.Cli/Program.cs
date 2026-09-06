using Sims4ModDoctor.Core.Duplicates;
using Sims4ModDoctor.Core.Reporting;

return await DuplicateScanCommand.RunAsync(args);

internal static class DuplicateScanCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return 0;
        }

        if (!string.Equals(args[0], "scan-duplicates", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"Unknown command: {args[0]}");
            PrintUsage();
            return 2;
        }

        try
        {
            var options = Parse(args[1..]);
            var scanner = DuplicateScanner.CreateDefault();
            var sources = options.Sources
                .Select((path, index) => new ScanSource($"source-{index + 1}", path, Order: index, Label: path))
                .ToArray();
            var report = await scanner.ScanAsync(new DuplicateScanRequest(
                sources,
                options.ModsRoot,
                options.Extensions));

            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var jsonPath = options.JsonPath ?? Path.Combine(Environment.CurrentDirectory, $"duplicate-report-{timestamp}.json");
            var htmlPath = options.HtmlPath ?? Path.Combine(Environment.CurrentDirectory, $"duplicate-report-{timestamp}.html");
            await DuplicateReportFiles.WriteAsync(report, jsonPath, htmlPath);

            Console.WriteLine($"Discovered: {report.DiscoveredFileCount}");
            Console.WriteLine($"Hashed: {report.HashedFileCount}");
            Console.WriteLine($"Duplicate groups: {report.Groups.Count}");
            Console.WriteLine($"Incomplete: {report.Issues.Count}");
            Console.WriteLine($"JSON: {Path.GetFullPath(jsonPath)}");
            Console.WriteLine($"HTML: {Path.GetFullPath(htmlPath)}");
            return report.Issues.Count == 0 ? 0 : 1;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            PrintUsage();
            return 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static Options Parse(string[] args)
    {
        var sources = new List<string>();
        var extensions = new List<string>();
        string? modsRoot = null;
        string? jsonPath = null;
        string? htmlPath = null;

        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            var value = index + 1 < args.Length ? args[index + 1] : null;
            if (value is null || value.StartsWith("-", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Missing value for {option}.");
            }

            index++;
            switch (option)
            {
                case "--source":
                    sources.Add(value);
                    break;
                case "--mods-root":
                    modsRoot = value;
                    break;
                case "--extension":
                    extensions.Add(value);
                    break;
                case "--json":
                    jsonPath = value;
                    break;
                case "--html":
                    htmlPath = value;
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {option}");
            }
        }

        if (sources.Count == 0)
        {
            throw new ArgumentException("At least one --source directory is required.");
        }

        return new Options(sources, extensions, modsRoot, jsonPath, htmlPath);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Sims 4 Mod Doctor duplicate scan");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  Sims4ModDoctor.Cli scan-duplicates --source <directory> [--source <directory> ...]");
        Console.WriteLine("    [--mods-root <directory>] [--extension .package] [--json <file>] [--html <file>]");
    }

    private sealed record Options(
        IReadOnlyList<string> Sources,
        IReadOnlyList<string> Extensions,
        string? ModsRoot,
        string? JsonPath,
        string? HtmlPath);
}
